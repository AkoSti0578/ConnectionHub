using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ConnectionHub.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ConnectionHub.Ssh;

public sealed record AuthPrompt(string Text, bool Echo);

/// <summary>User interaction needed while connecting. Implementations are called from a background thread.</summary>
public interface ISshInteraction
{
    /// <summary>Asks whether an unknown or changed host key may be trusted.</summary>
    bool ConfirmHostKey(string host, int port, string keyType, string fingerprint, string? previousFingerprint);

    /// <summary>Shows keyboard-interactive prompts (password, OTP / 2FA codes, ...). Returns null when cancelled.</summary>
    string[]? PromptKeyboardInteractive(string title, string instruction, IReadOnlyList<AuthPrompt> prompts);

    /// <summary>Asks for a password or key passphrase. Returns null when cancelled.</summary>
    string? PromptSecret(string title, string message);
}

public sealed class SshConnectOptions
{
    public required ConnectionEntry Entry { get; init; }
    public string? Password { get; init; }
    public string? KeyPassphrase { get; init; }
    public required Func<string, string?> KnownHostLookup { get; init; }
    public required Action<string, string> KnownHostStore { get; init; }
}

/// <summary>
/// Shared SSH login logic for terminal and SFTP sessions: public key, password and keyboard-interactive
/// (2FA / OTP) authentication plus host key verification against the stored known hosts.
/// </summary>
public sealed class SshAuthenticator
{
    private static readonly Regex PasswordPrompt = new(@"pass(word|code|phrase)?\s*(for .*)?:\s*$|wachtwoord", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SshConnectOptions _options;
    private readonly ISshInteraction _interaction;
    private bool _passwordUsedForKeyboardInteractive;
    private bool _cancelledByUser;

    public SshAuthenticator(SshConnectOptions options, ISshInteraction interaction)
    {
        _options = options;
        _interaction = interaction;
    }

    public ConnectionInfo CreateConnectionInfo()
    {
        var entry = _options.Entry;
        return new ConnectionInfo(entry.Host, entry.Port, entry.Username, BuildAuthenticationMethods(entry).ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    /// <summary>Connects the client with host key verification; maps a user cancel to <see cref="OperationCanceledException"/>.</summary>
    public void Connect(BaseClient client)
    {
        client.HostKeyReceived += OnHostKeyReceived;
        if (_options.Entry.Ssh.KeepAliveSeconds > 0)
            client.KeepAliveInterval = TimeSpan.FromSeconds(_options.Entry.Ssh.KeepAliveSeconds);

        try
        {
            client.Connect();
        }
        catch (Exception) when (_cancelledByUser)
        {
            throw new OperationCanceledException("Aanmelden geannuleerd.");
        }
    }

    private List<AuthenticationMethod> BuildAuthenticationMethods(ConnectionEntry entry)
    {
        var methods = new List<AuthenticationMethod>();
        var mode = entry.Ssh.AuthMode;

        if (mode is SshAuthMode.Auto or SshAuthMode.PublicKey && !string.IsNullOrWhiteSpace(entry.Ssh.PrivateKeyPath))
        {
            methods.Add(new PrivateKeyAuthenticationMethod(entry.Username, LoadPrivateKey(entry.Ssh.PrivateKeyPath)));
        }
        else if (mode == SshAuthMode.PublicKey)
        {
            throw new InvalidOperationException("Er is geen private key ingesteld voor deze verbinding.");
        }

        if (mode is SshAuthMode.Auto or SshAuthMode.Password && !string.IsNullOrEmpty(_options.Password))
        {
            var pw = new PasswordAuthenticationMethod(entry.Username, _options.Password);
            pw.PasswordExpired += OnPasswordExpired;
            methods.Add(pw);
        }

        if (mode is SshAuthMode.Auto or SshAuthMode.KeyboardInteractive or SshAuthMode.PublicKey or SshAuthMode.Password)
        {
            // Keyboard-interactive is always offered: it handles 2FA / OTP prompts that follow a
            // successful first factor (partial success), as well as plain password prompts.
            var ki = new KeyboardInteractiveAuthenticationMethod(entry.Username);
            ki.AuthenticationPrompt += OnKeyboardInteractivePrompt;
            methods.Add(ki);
        }

        return methods;
    }

    private PrivateKeyFile LoadPrivateKey(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Private key niet gevonden.", path);

        try
        {
            return string.IsNullOrEmpty(_options.KeyPassphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, _options.KeyPassphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            var passphrase = _interaction.PromptSecret("Passphrase vereist", $"Voer de passphrase in voor {Path.GetFileName(path)}")
                             ?? throw new OperationCanceledException("Aanmelden geannuleerd.");
            return new PrivateKeyFile(path, passphrase);
        }
    }

    private void OnKeyboardInteractivePrompt(object? sender, AuthenticationPromptEventArgs e)
    {
        var prompts = e.Prompts.ToList();
        if (prompts.Count == 0) return;

        if (_cancelledByUser)
        {
            // The server may keep asking after a cancel; answer empty until it gives up.
            foreach (var p in prompts) p.Response = string.Empty;
            return;
        }

        // Answer a single password prompt automatically (once) when a password is stored,
        // so only the second factor (OTP / push / token) has to be entered by the user.
        if (!_passwordUsedForKeyboardInteractive && !string.IsNullOrEmpty(_options.Password)
            && prompts.Count == 1 && !prompts[0].IsEchoed && PasswordPrompt.IsMatch(prompts[0].Request))
        {
            _passwordUsedForKeyboardInteractive = true;
            prompts[0].Response = _options.Password;
            return;
        }

        var answers = _interaction.PromptKeyboardInteractive(
            $"{_options.Entry.Username}@{_options.Entry.Host}",
            e.Instruction ?? string.Empty,
            prompts.Select(p => new AuthPrompt(p.Request.Trim(), p.IsEchoed)).ToList());

        if (answers == null)
        {
            _cancelledByUser = true;
            foreach (var p in prompts) p.Response = string.Empty;
            return;
        }

        for (var i = 0; i < prompts.Count; i++)
            prompts[i].Response = i < answers.Length ? answers[i] : string.Empty;
    }

    private void OnPasswordExpired(object? sender, AuthenticationPasswordChangeEventArgs e)
    {
        var newPassword = _interaction.PromptSecret("Wachtwoord verlopen", "Het wachtwoord is verlopen. Voer een nieuw wachtwoord in.");
        if (newPassword == null)
        {
            _cancelledByUser = true;
            return;
        }
        e.NewPassword = Encoding.UTF8.GetBytes(newPassword);
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var entry = _options.Entry;
        var key = $"[{entry.Host}]:{entry.Port}";
        var fingerprint = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
        var known = _options.KnownHostLookup(key);

        if (string.Equals(known, fingerprint, StringComparison.Ordinal))
        {
            e.CanTrust = true;
            return;
        }

        e.CanTrust = _interaction.ConfirmHostKey(entry.Host, entry.Port, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256, known);
        if (e.CanTrust)
            _options.KnownHostStore(key, fingerprint);
        else
            _cancelledByUser = true;
    }
}
