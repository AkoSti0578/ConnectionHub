using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ConnectionHub.Models;

namespace ConnectionHub.Services;

/// <summary>Opens an RDP connection in the standalone Windows client (mstsc.exe), e.g. for multi-monitor use.</summary>
public static class ExternalRdpLauncher
{
    public static void Launch(ConnectionEntry entry, bool multiMonitor)
    {
        var s = entry.Rdp;
        var address = entry.Port == 3389 ? entry.Host : $"{entry.Host}:{entry.Port}";
        var user = string.IsNullOrEmpty(entry.Domain) ? entry.Username : $"{entry.Domain}\\{entry.Username}";

        var rdp = new StringBuilder();
        rdp.AppendLine($"full address:s:{address}");
        if (!string.IsNullOrEmpty(user)) rdp.AppendLine($"username:s:{user}");
        rdp.AppendLine($"screen mode id:i:{(multiMonitor ? 2 : 1)}");
        rdp.AppendLine($"use multimon:i:{(multiMonitor ? 1 : 0)}");
        rdp.AppendLine($"dynamic resolution:i:{(s.ScalingMode == RdpScalingMode.DynamicResolution ? 1 : 0)}");
        rdp.AppendLine($"smart sizing:i:{(s.ScalingMode == RdpScalingMode.SmartSizing ? 1 : 0)}");
        if (s.ScalingMode != RdpScalingMode.DynamicResolution)
        {
            rdp.AppendLine($"desktopwidth:i:{s.DesktopWidth}");
            rdp.AppendLine($"desktopheight:i:{s.DesktopHeight}");
        }
        if (s.ScaleFactor > 0) rdp.AppendLine($"desktopscalefactor:i:{s.ScaleFactor}");
        rdp.AppendLine($"session bpp:i:{s.ColorDepth}");
        rdp.AppendLine($"redirectclipboard:i:{(s.RedirectClipboard ? 1 : 0)}");
        rdp.AppendLine($"redirectprinters:i:{(s.RedirectPrinters ? 1 : 0)}");
        rdp.AppendLine($"redirectsmartcards:i:{(s.RedirectSmartCards ? 1 : 0)}");
        rdp.AppendLine($"drivestoredirect:s:{(s.RedirectDrives ? "*" : string.Empty)}");
        rdp.AppendLine($"audiomode:i:{(s.RedirectAudio ? 0 : 2)}");
        rdp.AppendLine($"enablecredsspsupport:i:{(s.UseNla ? 1 : 0)}");
        rdp.AppendLine($"administrative session:i:{(s.AdminSession ? 1 : 0)}");
        rdp.AppendLine("autoreconnection enabled:i:1");
        rdp.AppendLine("authentication level:i:2");
        if (!string.IsNullOrWhiteSpace(s.GatewayHost))
        {
            rdp.AppendLine($"gatewayhostname:s:{s.GatewayHost}");
            rdp.AppendLine("gatewayusagemethod:i:1");
            rdp.AppendLine("gatewayprofileusagemethod:i:1");
            rdp.AppendLine($"promptcredentialonce:i:{(s.GatewayUseSameCredentials ? 1 : 0)}");
        }

        var dir = Path.Combine(Path.GetTempPath(), "ConnectionHub");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{entry.Id:N}.rdp");
        File.WriteAllText(file, rdp.ToString(), Encoding.Unicode);

        // Hand the stored password to mstsc through the Windows credential manager.
        var password = CredentialProtector.Unprotect(entry.ProtectedPassword);
        if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(user))
        {
            WriteGenericCredential($"TERMSRV/{entry.Host}", user, password);
        }

        Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{file}\"") { UseShellExecute = true });
    }

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    /// <summary>Same result as <c>cmdkey /generic:TERMSRV/host</c>, without exposing the password on a command line.</summary>
    private static void WriteGenericCredential(string target, string user, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = Encoding.Unicode.GetByteCount(password),
                Persist = CredPersistLocalMachine
            };
            CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }
}
