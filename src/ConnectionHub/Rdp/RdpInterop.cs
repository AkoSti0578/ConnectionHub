using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ConnectionHub.Rdp;

/// <summary>
/// Event dispinterface of the Remote Desktop ActiveX control (mstscax.dll).
/// Only the events Connection Hub uses are declared; unknown DISPIDs are ignored by the CLR.
/// DISPIDs: https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-interface
/// </summary>
[ComVisible(true)]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(17)] void OnAutoReconnecting(int disconnectReason, int attemptCount, out int pArcContinueStatus);
    [DispId(22)] void OnLogonError(int lError);
    [DispId(33)] void OnAutoReconnected();
}

/// <summary>Vtable-only interface for named extended settings (DesktopScaleFactor, DeviceScaleFactor, ...).</summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    void set_Property([In, MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [In] ref object pValue);

    [return: MarshalAs(UnmanagedType.Struct)]
    object get_Property([In, MarshalAs(UnmanagedType.BStr)] string bstrPropertyName);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IMsTscAxEvents
{
    private readonly RdpClientHost _owner;

    public RdpEventSink(RdpClientHost owner) => _owner = owner;

    public void OnConnecting() => _owner.RaiseConnecting();
    public void OnConnected() => _owner.RaiseConnected();
    public void OnLoginComplete() => _owner.RaiseLoginComplete();
    public void OnDisconnected(int discReason) => _owner.RaiseDisconnected(discReason);
    public void OnEnterFullScreenMode() => _owner.RaiseFullScreenChanged(true);
    public void OnLeaveFullScreenMode() => _owner.RaiseFullScreenChanged(false);
    public void OnFatalError(int errorCode) => _owner.RaiseFatalError(errorCode);
    public void OnWarning(int warningCode) { }
    public void OnRemoteDesktopSizeChange(int width, int height) { }
    public void OnRequestContainerMinimize() => _owner.RaiseRequestMinimize();

    public void OnAutoReconnecting(int disconnectReason, int attemptCount, out int pArcContinueStatus)
    {
        // 0 = autoReconnectContinueAutomatic
        pArcContinueStatus = 0;
        _owner.RaiseAutoReconnecting(attemptCount);
    }

    public void OnLogonError(int lError) { }
    public void OnAutoReconnected() => _owner.RaiseLoginComplete();
}

/// <summary>
/// Hosts MsRdpClient9NotSafeForScripting (Windows 8.1+) without a generated AxInterop assembly.
/// Properties and methods are reached through IDispatch (<c>dynamic</c>).
/// </summary>
public sealed class RdpClientHost : AxHost
{
    public const string ClsidMsRdpClient9NotSafeForScripting = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";

    private ConnectionPointCookie? _cookie;
    private RdpEventSink? _sink;

    public RdpClientHost() : base(ClsidMsRdpClient9NotSafeForScripting)
    {
    }

    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? LoginComplete;
    public event EventHandler<int>? Disconnected;
    public event EventHandler<int>? FatalError;
    public event EventHandler<bool>? FullScreenChanged;
    public event EventHandler? RequestMinimize;
    public event EventHandler<int>? AutoReconnecting;

    /// <summary>The underlying COM object, accessed late-bound.</summary>
    public dynamic Ocx => GetOcx() ?? throw new InvalidOperationException("RDP control is not created yet.");

    protected override void CreateSink()
    {
        try
        {
            _sink = new RdpEventSink(this);
            _cookie = new ConnectionPointCookie(GetOcx()!, _sink, typeof(IMsTscAxEvents));
        }
        catch (Exception)
        {
            _cookie = null;
        }
    }

    protected override void DetachSink()
    {
        try { _cookie?.Disconnect(); }
        catch (Exception) { /* control already gone */ }
        _cookie = null;
    }

    public void SetExtendedProperty(string name, object value)
    {
        if (GetOcx() is IMsRdpExtendedSettings ext)
        {
            ext.set_Property(name, ref value);
        }
    }

    internal void RaiseConnecting() => Connecting?.Invoke(this, EventArgs.Empty);
    internal void RaiseConnected() => Connected?.Invoke(this, EventArgs.Empty);
    internal void RaiseLoginComplete() => LoginComplete?.Invoke(this, EventArgs.Empty);
    internal void RaiseDisconnected(int reason) => Disconnected?.Invoke(this, reason);
    internal void RaiseFatalError(int code) => FatalError?.Invoke(this, code);
    internal void RaiseFullScreenChanged(bool fullScreen) => FullScreenChanged?.Invoke(this, fullScreen);
    internal void RaiseRequestMinimize() => RequestMinimize?.Invoke(this, EventArgs.Empty);
    internal void RaiseAutoReconnecting(int attempt) => AutoReconnecting?.Invoke(this, attempt);
}
