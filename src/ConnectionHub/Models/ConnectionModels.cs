using System;
using System.Collections.Generic;

namespace ConnectionHub.Models;

public enum ConnectionProtocol
{
    Rdp,
    Ssh
}

/// <summary>How the remote desktop adapts to the size of the session tab.</summary>
public enum RdpScalingMode
{
    /// <summary>The remote resolution follows the window size (requires Windows 8.1 / Server 2012 R2 or newer on the host).</summary>
    DynamicResolution,
    /// <summary>The remote desktop keeps its resolution and is scaled to fit the window.</summary>
    SmartSizing,
    /// <summary>Fixed resolution, scroll bars when the window is smaller.</summary>
    Fixed
}

public enum SshAuthMode
{
    /// <summary>Tries public key (if configured), password and keyboard-interactive (2FA / OTP).</summary>
    Auto,
    Password,
    KeyboardInteractive,
    PublicKey
}

public sealed class ConnectionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nieuwe groep";
    public Guid? ParentId { get; set; }
    public string Color { get; set; } = "#6366F1";
    public bool IsExpanded { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class RdpSettings
{
    public RdpScalingMode ScalingMode { get; set; } = RdpScalingMode.DynamicResolution;
    public int DesktopWidth { get; set; } = 1920;
    public int DesktopHeight { get; set; } = 1080;
    public int ColorDepth { get; set; } = 32;
    /// <summary>Remote DPI scale in percent (100, 125, 150, 175, 200). 0 = follow local monitor.</summary>
    public int ScaleFactor { get; set; }
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectSmartCards { get; set; }
    public bool RedirectAudio { get; set; } = true;
    public bool UseNla { get; set; } = true;
    public bool AdminSession { get; set; }
    public string GatewayHost { get; set; } = string.Empty;
    public bool GatewayUseSameCredentials { get; set; } = true;
}

public sealed class SshSettings
{
    public SshAuthMode AuthMode { get; set; } = SshAuthMode.KeyboardInteractive;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string? ProtectedKeyPassphrase { get; set; }
    public string TerminalType { get; set; } = "xterm-256color";
    public int FontSize { get; set; } = 14;
    public int KeepAliveSeconds { get; set; } = 30;
}

public sealed class ConnectionEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ConnectionProtocol Protocol { get; set; } = ConnectionProtocol.Rdp;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    /// <summary>DPAPI (CurrentUser) protected password, base64 encoded.</summary>
    public string? ProtectedPassword { get; set; }
    public Guid? GroupId { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime? LastConnected { get; set; }
    public RdpSettings Rdp { get; set; } = new();
    public SshSettings Ssh { get; set; } = new();

    public static int DefaultPort(ConnectionProtocol protocol) => protocol == ConnectionProtocol.Rdp ? 3389 : 22;
}

public sealed class ConnectionDatabase
{
    public int Version { get; set; } = 1;
    public List<ConnectionGroup> Groups { get; set; } = new();
    public List<ConnectionEntry> Connections { get; set; } = new();
    public Dictionary<string, string> KnownHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
