using System;
using System.Windows;
using System.Windows.Input;
using ConnectionHub.Models;

namespace ConnectionHub.ViewModels;

public enum SessionState
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    Failed
}

/// <summary>A UI element that hosts one remote session (RDP control or SSH terminal).</summary>
public interface ISessionView : IDisposable
{
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    FrameworkElement Element { get; }
    bool SupportsFullScreen { get; }
    void Connect();
    void Disconnect();
    void EnterFullScreen();
    void FocusSession();
}

public sealed class SessionStateChangedEventArgs : EventArgs
{
    public SessionStateChangedEventArgs(SessionState state, string? message = null)
    {
        State = state;
        Message = message;
    }

    public SessionState State { get; }
    public string? Message { get; }
}

public sealed class SessionViewModel : ObservableObject, IDisposable
{
    private SessionState _state = SessionState.Idle;
    private string _statusText = string.Empty;
    private bool _isActive;

    public SessionViewModel(ConnectionEntry entry, ISessionView view)
    {
        Entry = entry;
        View = view;
        View.StateChanged += (_, e) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            State = e.State;
            StatusText = e.Message ?? DefaultStatus(e.State);
        });

        ReconnectCommand = new RelayCommand(() => { View.Disconnect(); View.Connect(); });
        DisconnectCommand = new RelayCommand(() => View.Disconnect(), () => State is SessionState.Connected or SessionState.Connecting);
        FullScreenCommand = new RelayCommand(() => View.EnterFullScreen(), () => View.SupportsFullScreen && State == SessionState.Connected);
    }

    public ConnectionEntry Entry { get; }
    public ISessionView View { get; }
    public FrameworkElement Element => View.Element;

    public string Title => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Host : Entry.Name;
    public string ProtocolLabel => Entry.Protocol == ConnectionProtocol.Rdp ? "RDP" : "SSH";
    public ConnectionProtocol Protocol => Entry.Protocol;

    public ICommand ReconnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand FullScreenCommand { get; }

    public SessionState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value)
                Application.Current.Dispatcher.BeginInvoke(View.FocusSession, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    public void Start() => View.Connect();

    public void Dispose() => View.Dispose();

    private static string DefaultStatus(SessionState state) => state switch
    {
        SessionState.Connecting => "Verbinden…",
        SessionState.Connected => "Verbonden",
        SessionState.Disconnected => "Verbinding verbroken",
        SessionState.Failed => "Verbinding mislukt",
        _ => string.Empty
    };
}
