using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectionHub.Models;
using ConnectionHub.Rdp;
using ConnectionHub.Services;
using ConnectionHub.ViewModels;

namespace ConnectionHub.Views;

/// <summary>
/// RDP session tab hosting the Remote Desktop ActiveX control.
/// Scaling: dynamic resolution follows the tab size (and DPI), smart sizing scales a fixed resolution.
/// </summary>
public sealed class RdpSessionView : Grid, ISessionView
{
    private static readonly int[] DesktopScaleSteps = { 100, 125, 150, 175, 200, 250, 300, 400, 500 };

    private readonly ConnectionEntry _entry;
    private readonly WindowsFormsHost _host;
    private readonly System.Windows.Forms.Panel _panel;
    private readonly RdpClientHost _rdp;
    private readonly DispatcherTimer _resizeTimer;
    private bool _connectRequested;
    private bool _loggedIn;
    private bool _disposed;
    private bool _dpiHooked;
    private int _lastWidth, _lastHeight;

    public RdpSessionView(ConnectionEntry entry)
    {
        _entry = entry;
        Background = new SolidColorBrush(Color.FromRgb(0x0f, 0x11, 0x17));

        _panel = new System.Windows.Forms.Panel
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(0x0f, 0x11, 0x17)
        };
        _rdp = new RdpClientHost { Dock = System.Windows.Forms.DockStyle.Fill };
        ((System.ComponentModel.ISupportInitialize)_rdp).BeginInit();
        _panel.Controls.Add(_rdp);
        ((System.ComponentModel.ISupportInitialize)_rdp).EndInit();

        _host = new WindowsFormsHost { Child = _panel };
        Children.Add(_host);

        _rdp.Connecting += (_, _) => Report(SessionState.Connecting, $"Verbinden met {_entry.Host}…");
        _rdp.Connected += (_, _) => Report(SessionState.Connecting, "Aanmelden…");
        _rdp.LoginComplete += (_, _) => OnLoginComplete();
        _rdp.Disconnected += (_, reason) => OnDisconnected(reason);
        _rdp.FatalError += (_, code) => Report(SessionState.Failed, $"Fatale fout in RDP-client (code {code})");
        _rdp.AutoReconnecting += (_, attempt) => Report(SessionState.Connecting, $"Automatisch opnieuw verbinden (poging {attempt})…");
        _rdp.RequestMinimize += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.WindowState = WindowState.Minimized;
        };

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            UpdateRemoteResolution();
        };

        SizeChanged += (_, _) =>
        {
            if (_loggedIn && _entry.Rdp.ScalingMode == RdpScalingMode.DynamicResolution)
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        };
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window && !_dpiHooked)
            {
                window.DpiChanged += (_, _) => UpdateRemoteResolution(force: true);
                _dpiHooked = true;
            }
            if (_connectRequested) Dispatcher.BeginInvoke(ConnectCore, DispatcherPriority.Loaded);
        };
    }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public FrameworkElement Element => this;
    public bool SupportsFullScreen => true;

    private bool IsConnected
    {
        get
        {
            try { return _rdp.Created && (int)_rdp.Ocx.Connected != 0; }
            catch (Exception) { return false; }
        }
    }

    public void Connect()
    {
        _connectRequested = true;
        if (IsLoaded) ConnectCore();
    }

    public void Disconnect()
    {
        _connectRequested = false;
        _loggedIn = false;
        if (!IsConnected) return;
        try { _rdp.Ocx.Disconnect(); }
        catch (Exception) { /* already disconnected */ }
    }

    public void EnterFullScreen()
    {
        if (!IsConnected) return;
        try { _rdp.Ocx.FullScreen = true; }
        catch (Exception ex) { Report(SessionState.Connected, "Volledig scherm niet beschikbaar: " + ex.Message); }
    }

    public void FocusSession()
    {
        if (_rdp.Created) _rdp.Focus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resizeTimer.Stop();
        Disconnect();
        try
        {
            _rdp.Dispose();
            _panel.Dispose();
            _host.Dispose();
        }
        catch (Exception)
        {
            // The ActiveX control may already have torn itself down.
        }
    }

    private void ConnectCore()
    {
        if (_disposed || IsConnected) return;

        try
        {
            if (!_rdp.Created) _rdp.CreateControl();
            Configure();
            Report(SessionState.Connecting, $"Verbinden met {_entry.Host}…");
            _rdp.Ocx.Connect();
            _entry.LastConnected = DateTime.Now;
            App.Store.Save();
        }
        catch (Exception ex)
        {
            Report(SessionState.Failed, "RDP-verbinding kon niet worden gestart: " + ex.Message);
        }
    }

    private void Configure()
    {
        var s = _entry.Rdp;
        dynamic ocx = _rdp.Ocx;

        ocx.Server = _entry.Host;
        if (!string.IsNullOrWhiteSpace(_entry.Username)) ocx.UserName = _entry.Username;
        if (!string.IsNullOrWhiteSpace(_entry.Domain)) ocx.Domain = _entry.Domain;
        TrySet(() => ocx.ConnectingText = $"Verbinden met {_entry.Host}…");
        TrySet(() => ocx.FullScreenTitle = $"{Title} – Connection Hub");
        TrySet(() => ocx.ColorDepth = s.ColorDepth is 15 or 16 or 24 or 32 ? s.ColorDepth : 32);

        var (width, height) = s.ScalingMode == RdpScalingMode.DynamicResolution
            ? CurrentPixelSize()
            : (Even(Math.Clamp(s.DesktopWidth, 640, 8192)), Math.Clamp(s.DesktopHeight, 480, 8192));
        ocx.DesktopWidth = width;
        ocx.DesktopHeight = height;
        _lastWidth = width;
        _lastHeight = height;

        dynamic adv = ocx.AdvancedSettings9;
        TrySet(() => adv.RDPPort = _entry.Port is > 0 and < 65536 ? _entry.Port : 3389);
        var password = CredentialProtector.Unprotect(_entry.ProtectedPassword);
        if (!string.IsNullOrEmpty(password)) TrySet(() => adv.ClearTextPassword = password);

        TrySet(() => adv.EnableCredSspSupport = s.UseNla);
        TrySet(() => adv.AuthenticationLevel = 2u); // warn when server authentication fails
        TrySet(() => adv.SmartSizing = s.ScalingMode == RdpScalingMode.SmartSizing);
        TrySet(() => adv.RedirectClipboard = s.RedirectClipboard);
        TrySet(() => adv.RedirectDrives = s.RedirectDrives);
        TrySet(() => adv.RedirectPrinters = s.RedirectPrinters);
        TrySet(() => adv.RedirectSmartCards = s.RedirectSmartCards);
        TrySet(() => adv.ConnectToAdministerServer = s.AdminSession);
        TrySet(() => adv.EnableAutoReconnect = true);
        TrySet(() => adv.MaxReconnectAttempts = 20);
        TrySet(() => adv.BitmapPersistence = 1);
        TrySet(() => adv.Compress = 1);
        TrySet(() => adv.DisplayConnectionBar = true);
        TrySet(() => adv.ContainerHandledFullScreen = 0);
        TrySet(() => adv.GrabFocusOnConnect = true);
        TrySet(() => adv.EnableWindowsKey = 1);
        TrySet(() => adv.allowBackgroundInput = 1);
        TrySet(() => adv.BandwidthDetection = true);
        TrySet(() => adv.NetworkConnectionType = 7u); // auto-detect

        // 0 = play on this computer, 2 = do not play
        TrySet(() => ocx.SecuredSettings3.AudioRedirectionMode = s.RedirectAudio ? 0 : 2);
        // 1 = apply Windows key combinations on the remote computer
        TrySet(() => ocx.SecuredSettings3.KeyboardHookMode = 1);

        if (!string.IsNullOrWhiteSpace(s.GatewayHost))
        {
            dynamic ts = ocx.TransportSettings2;
            TrySet(() => ts.GatewayHostname = s.GatewayHost);
            TrySet(() => ts.GatewayUsageMethod = 1u);         // always use the gateway
            TrySet(() => ts.GatewayProfileUsageMethod = 1u);  // explicit settings
            TrySet(() => ts.GatewayCredsSource = 4u);         // any supported method
            TrySet(() => ts.GatewayCredSharing = s.GatewayUseSameCredentials ? 1u : 0u);
        }

        var (desktopScale, deviceScale) = ScaleFactors();
        TrySet(() => _rdp.SetExtendedProperty("DesktopScaleFactor", (uint)desktopScale));
        TrySet(() => _rdp.SetExtendedProperty("DeviceScaleFactor", (uint)deviceScale));
    }

    private string Title => string.IsNullOrWhiteSpace(_entry.Name) ? _entry.Host : _entry.Name;

    private void OnLoginComplete()
    {
        _loggedIn = true;
        Report(SessionState.Connected, $"Verbonden met {_entry.Host}");
        if (_entry.Rdp.ScalingMode == RdpScalingMode.DynamicResolution)
            Dispatcher.BeginInvoke(() => UpdateRemoteResolution(force: true), DispatcherPriority.Background);
    }

    private void OnDisconnected(int reason)
    {
        _loggedIn = false;
        // 1 = local disconnect, 2 = remote disconnect by user, 3 = remote disconnect by server (logoff)
        if (reason is 0 or 1 or 2 or 3 || !_connectRequested)
        {
            Report(SessionState.Disconnected, "Verbinding verbroken");
            return;
        }

        string message;
        try
        {
            message = (string)_rdp.Ocx.GetErrorDescription((uint)reason, (uint)(int)_rdp.Ocx.ExtendedDisconnectReason);
        }
        catch (Exception)
        {
            message = string.Empty;
        }
        if (string.IsNullOrWhiteSpace(message)) message = $"Verbinding verbroken (code {reason}).";
        Report(SessionState.Failed, message.Trim());
    }

    /// <summary>Resizes the remote desktop to the current tab size (dynamic resolution).</summary>
    private void UpdateRemoteResolution(bool force = false)
    {
        if (!_loggedIn || _disposed || _entry.Rdp.ScalingMode != RdpScalingMode.DynamicResolution) return;

        var (width, height) = CurrentPixelSize();
        if (!force && width == _lastWidth && height == _lastHeight) return;

        var (desktopScale, deviceScale) = ScaleFactors();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        var physicalWidth = (uint)Math.Round(width * 25.4 / dpi);
        var physicalHeight = (uint)Math.Round(height * 25.4 / dpi);

        try
        {
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)width, (uint)height, physicalWidth, physicalHeight, 0u, (uint)desktopScale, (uint)deviceScale);
            _lastWidth = width;
            _lastHeight = height;
        }
        catch (Exception)
        {
            // Hosts without display-control support: fall back to a reconnect with the new size.
            try
            {
                _rdp.Ocx.Reconnect((uint)width, (uint)height);
                _lastWidth = width;
                _lastHeight = height;
            }
            catch (Exception)
            {
                // Resolution stays as is.
            }
        }
    }

    private (int Width, int Height) CurrentPixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(Math.Max(ActualWidth, 200) * dpi.DpiScaleX);
        var height = (int)Math.Round(Math.Max(ActualHeight, 200) * dpi.DpiScaleY);
        return (Even(Math.Clamp(width, 200, 8192)), Math.Clamp(height, 200, 8192));
    }

    private (int Desktop, int Device) ScaleFactors()
    {
        var configured = _entry.Rdp.ScaleFactor;
        var desktop = configured > 0 ? configured : (int)Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
        desktop = Nearest(DesktopScaleSteps, Math.Clamp(desktop, 100, 500));
        var device = desktop < 140 ? 100 : desktop < 180 ? 140 : 180;
        return (desktop, device);
    }

    private static int Nearest(int[] values, int target)
    {
        var best = values[0];
        foreach (var v in values)
            if (Math.Abs(v - target) < Math.Abs(best - target)) best = v;
        return best;
    }

    private static int Even(int value) => value - (value % 2);

    private static void TrySet(Action action)
    {
        try { action(); }
        catch (Exception) { /* property not supported by this mstscax version */ }
    }

    private void Report(SessionState state, string message)
        => StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
}
