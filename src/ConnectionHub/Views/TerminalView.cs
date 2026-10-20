using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConnectionHub.Models;
using ConnectionHub.Services;
using ConnectionHub.Ssh;
using ConnectionHub.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Renci.SshNet.Common;

namespace ConnectionHub.Views;

/// <summary>SSH session tab: xterm.js inside WebView2, connected to an <see cref="SshTerminalSession"/>.</summary>
public sealed class TerminalView : Grid, ISessionView
{
    private const string VirtualHost = "terminal.connectionhub.local";
    private static Task<CoreWebView2Environment>? _environment;

    private readonly ConnectionEntry _entry;
    private readonly WebView2 _webView = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0f, 0x11, 0x17) };
    private readonly TextBlock _error = new()
    {
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24),
        Visibility = Visibility.Collapsed
    };
    private readonly StringBuilder _pending = new();
    private readonly object _pendingLock = new();

    private SshTerminalSession? _session;
    private bool _webInitStarted;
    private bool _webReady;
    private bool _terminalReady;
    private bool _connectRequested;
    private bool _flushScheduled;
    private bool _disposed;
    private uint _cols = 120, _rows = 30;

    public TerminalView(ConnectionEntry entry)
    {
        _entry = entry;
        Background = new SolidColorBrush(Color.FromRgb(0x0f, 0x11, 0x17));
        Children.Add(_webView);
        Children.Add(_error);
        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public FrameworkElement Element => this;
    public bool SupportsFullScreen => false;

    public void Connect()
    {
        _connectRequested = true;
        if (_terminalReady) _ = StartSessionAsync();
    }

    public void Disconnect()
    {
        _connectRequested = false;
        var session = _session;
        if (session == null) return;
        _session = null;
        session.Dispose();
        PostStatus("Verbinding verbroken.");
        Report(SessionState.Disconnected, "Verbinding verbroken");
    }

    public void EnterFullScreen() { }

    public void FocusSession()
    {
        if (!_webReady) return;
        _webView.Focus();
        Post(new { type = "focus" });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _webView.Dispose();
    }

    private static Task<CoreWebView2Environment> GetEnvironment()
    {
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectionHub", "WebView2");
        return _environment ??= CoreWebView2Environment.CreateAsync(null, dataFolder);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webInitStarted || _disposed) return;
        _webInitStarted = true;
        try
        {
            await _webView.EnsureCoreWebView2Async(await GetEnvironment());
        }
        catch (Exception ex)
        {
            _webView.Visibility = Visibility.Collapsed;
            _error.Visibility = Visibility.Visible;
            _error.Text = "De terminal kon niet worden gestart. Controleer of de Microsoft Edge WebView2 Runtime is geïnstalleerd " +
                          "(https://go.microsoft.com/fwlink/p/?LinkId=2124703).\n\n" + ex.Message;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(SessionState.Failed, "WebView2 Runtime ontbreekt"));
            return;
        }

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"), CoreWebView2HostResourceAccessKind.Allow);
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenLink(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenLink(e.Uri);
            }
        };
        core.WebMessageReceived += OnWebMessage;
        _webReady = true;

        var fontSize = _entry.Ssh.FontSize is >= 8 and <= 32 ? _entry.Ssh.FontSize : 14;
        core.Navigate($"https://{VirtualHost}/terminal.html?fontSize={fontSize}");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _cols = (uint)Math.Max(10, root.GetProperty("cols").GetInt32());
                _rows = (uint)Math.Max(2, root.GetProperty("rows").GetInt32());
                _terminalReady = true;
                if (_connectRequested && _session == null) _ = StartSessionAsync();
                break;
            case "input":
                _session?.Write(root.GetProperty("data").GetString() ?? string.Empty);
                break;
            case "resize":
                _cols = (uint)Math.Max(10, root.GetProperty("cols").GetInt32());
                _rows = (uint)Math.Max(2, root.GetProperty("rows").GetInt32());
                _session?.Resize(_cols, _rows);
                break;
            case "copy":
                TrySetClipboard(root.GetProperty("data").GetString());
                break;
            case "pasteRequest":
                if (TryGetClipboardText() is { Length: > 0 } text) Post(new { type = "paste", data = text });
                break;
            case "openLink":
                OpenLink(root.GetProperty("uri").GetString());
                break;
        }
    }

    private async Task StartSessionAsync(string? passwordOverride = null)
    {
        Disconnect();
        _connectRequested = true;

        var password = passwordOverride ?? CredentialProtector.Unprotect(_entry.ProtectedPassword);
        var interaction = new DialogSshInteraction(Window.GetWindow(this));

        if (_entry.Ssh.AuthMode == SshAuthMode.Password && string.IsNullOrEmpty(password))
        {
            password = interaction.PromptSecret("Wachtwoord", $"Wachtwoord voor {_entry.Username}@{_entry.Host}");
            if (password == null)
            {
                Report(SessionState.Disconnected, "Aanmelden geannuleerd");
                return;
            }
        }

        var store = App.Store;
        var session = new SshTerminalSession(new SshConnectOptions
        {
            Entry = _entry,
            Password = password,
            KeyPassphrase = CredentialProtector.Unprotect(_entry.Ssh.ProtectedKeyPassphrase),
            KnownHostLookup = key => store.Data.KnownHosts.TryGetValue(key, out var v) ? v : null,
            KnownHostStore = (key, value) =>
            {
                store.Data.KnownHosts[key] = value;
                store.Save();
            }
        }, interaction);

        session.Output += QueueOutput;
        session.Closed += error => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            if (error != null)
            {
                PostStatus($"Verbinding verbroken: {error}");
                Report(SessionState.Failed, error);
            }
            else
            {
                PostStatus("Verbinding gesloten.");
                Report(SessionState.Disconnected, "Verbinding gesloten");
            }
        });
        _session = session;

        Report(SessionState.Connecting, $"Verbinden met {_entry.Host}:{_entry.Port}…");
        PostStatus($"Verbinden met {_entry.Username}@{_entry.Host}:{_entry.Port}…");

        try
        {
            await session.ConnectAsync(_cols, _rows);
            if (!ReferenceEquals(_session, session)) return;
            Report(SessionState.Connected, "Verbonden");
            _entry.LastConnected = DateTime.Now;
            App.Store.Save();
            FocusSession();
        }
        catch (SshAuthenticationException) when (passwordOverride == null && _entry.Ssh.AuthMode == SshAuthMode.Auto
                                                 && string.IsNullOrEmpty(password) && ReferenceEquals(_session, session))
        {
            // The server may only accept the "password" method; ask once and retry.
            var asked = interaction.PromptSecret("Wachtwoord", $"Wachtwoord voor {_entry.Username}@{_entry.Host}");
            if (asked == null)
            {
                Fail(session, "Aanmelden geannuleerd");
                return;
            }
            await StartSessionAsync(asked);
        }
        catch (OperationCanceledException ex)
        {
            Fail(session, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(session, ex.Message);
        }
    }

    private void Fail(SshTerminalSession session, string message)
    {
        if (!ReferenceEquals(_session, session)) return;
        _session = null;
        session.Dispose();
        PostStatus("Fout: " + message);
        Report(SessionState.Failed, message);
    }

    private void Report(SessionState state, string message)
        => StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));

    private void QueueOutput(string text)
    {
        lock (_pendingLock)
        {
            _pending.Append(text);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Dispatcher.BeginInvoke(FlushOutput, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushOutput()
    {
        string text;
        lock (_pendingLock)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }
        if (text.Length > 0) Post(new { type = "output", data = text });
    }

    private void PostStatus(string text) => Post(new { type = "status", data = text });

    private void Post(object message)
    {
        if (!_webReady || _disposed || _webView.CoreWebView2 == null) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private static void OpenLink(string? uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    private static void TrySetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch (Exception) { /* clipboard locked by another process */ }
    }

    private static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception) { return null; }
    }
}
