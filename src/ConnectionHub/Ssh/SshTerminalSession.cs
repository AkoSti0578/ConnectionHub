using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace ConnectionHub.Ssh;

/// <summary>An interactive SSH shell built on SSH.NET, supporting password, public key and keyboard-interactive (2FA) authentication.</summary>
public sealed class SshTerminalSession : IDisposable
{
    private readonly SshConnectOptions _options;
    private readonly SshAuthenticator _auth;
    private readonly CancellationTokenSource _cts = new();
    private SshClient? _client;
    private ShellStream? _shell;
    private int _closedRaised;

    public SshTerminalSession(SshConnectOptions options, ISshInteraction interaction)
    {
        _options = options;
        _auth = new SshAuthenticator(options, interaction);
    }

    /// <summary>Raised with decoded terminal output (UTF-8).</summary>
    public event Action<string>? Output;

    /// <summary>Raised once when the shell ends. The argument is an error message or null for a normal exit.</summary>
    public event Action<string?>? Closed;

    public bool IsConnected => _client?.IsConnected == true && _shell != null;

    public async Task ConnectAsync(uint columns, uint rows)
    {
        await Task.Run(() =>
        {
            var entry = _options.Entry;
            _client = new SshClient(_auth.CreateConnectionInfo());
            _auth.Connect(_client);

            var term = string.IsNullOrWhiteSpace(entry.Ssh.TerminalType) ? "xterm-256color" : entry.Ssh.TerminalType;
            _shell = _client.CreateShellStream(term, columns, rows, 0, 0, 64 * 1024);
            _shell.Closed += (_, _) => RaiseClosed(null);
            _client.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
        }, _cts.Token).ConfigureAwait(false);

        _ = Task.Run(ReadLoopAsync);
    }

    public void Write(string data)
    {
        var shell = _shell;
        if (shell == null || !IsConnected) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            shell.Write(bytes, 0, bytes.Length);
            shell.Flush();
        }
        catch (Exception ex)
        {
            RaiseClosed(ex.Message);
        }
    }

    public void Resize(uint columns, uint rows)
    {
        try
        {
            _shell?.ChangeWindowSize(columns, rows, 0, 0);
        }
        catch (Exception)
        {
            // Resize is best effort; the server may already be gone.
        }
    }

    public void Disconnect()
    {
        _cts.Cancel();
        try { _shell?.Dispose(); } catch (Exception) { }
        try { if (_client?.IsConnected == true) _client.Disconnect(); } catch (Exception) { }
        RaiseClosed(null);
    }

    public void Dispose()
    {
        Disconnect();
        _client?.Dispose();
        _cts.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var shell = _shell;
        if (shell == null) return;

        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await shell.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
            RaiseClosed(null);
        }
        catch (OperationCanceledException)
        {
            RaiseClosed(null);
        }
        catch (ObjectDisposedException)
        {
            RaiseClosed(null);
        }
        catch (Exception ex)
        {
            RaiseClosed(ex.Message);
        }
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            Closed?.Invoke(error);
    }
}
