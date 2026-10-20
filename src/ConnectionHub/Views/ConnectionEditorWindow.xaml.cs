using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ConnectionHub.Models;
using ConnectionHub.Services;
using Microsoft.Win32;

namespace ConnectionHub.Views;

public sealed record GroupOption(Guid? Id, string Label);

public partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionEntry _entry;
    private bool _initializing = true;

    public ConnectionEditorWindow(ConnectionEntry entry, bool isNew, IReadOnlyList<ConnectionGroup> groups)
    {
        InitializeComponent();
        WindowTheming.Attach(this);
        _entry = entry;

        HeaderText.Text = isNew ? "Nieuwe verbinding" : "Verbinding bewerken";
        Title = HeaderText.Text;

        var options = new List<GroupOption> { new(null, "(geen groep)") };
        options.AddRange(BuildGroupOptions(groups, null, string.Empty));
        GroupCombo.ItemsSource = options;
        GroupCombo.SelectedItem = options.FirstOrDefault(o => o.Id == entry.GroupId) ?? options[0];

        NameBox.Text = entry.Name;
        HostBox.Text = entry.Host;
        PortBox.Text = entry.Port.ToString();
        UserBox.Text = entry.Username;
        DomainBox.Text = entry.Domain;
        NotesBox.Text = entry.Notes;

        var hasPassword = !string.IsNullOrEmpty(entry.ProtectedPassword);
        PasswordHint.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
        ClearPasswordCheck.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
        if (!hasPassword)
            PasswordHint.Text = string.Empty;

        // RDP
        var rdp = entry.Rdp;
        ScalingCombo.SelectedIndex = (int)rdp.ScalingMode;
        WidthBox.Text = rdp.DesktopWidth.ToString();
        HeightBox.Text = rdp.DesktopHeight.ToString();
        SelectByTag(ScaleCombo, rdp.ScaleFactor);
        SelectByTag(ColorCombo, rdp.ColorDepth);
        ClipboardCheck.IsChecked = rdp.RedirectClipboard;
        AudioCheck.IsChecked = rdp.RedirectAudio;
        DrivesCheck.IsChecked = rdp.RedirectDrives;
        PrintersCheck.IsChecked = rdp.RedirectPrinters;
        SmartCardCheck.IsChecked = rdp.RedirectSmartCards;
        NlaCheck.IsChecked = rdp.UseNla;
        AdminCheck.IsChecked = rdp.AdminSession;
        GatewayBox.Text = rdp.GatewayHost;
        GatewaySameCredsCheck.IsChecked = rdp.GatewayUseSameCredentials;

        // SSH
        var ssh = entry.Ssh;
        AuthCombo.SelectedIndex = (int)ssh.AuthMode;
        KeyPathBox.Text = ssh.PrivateKeyPath;
        PassphraseHint.Visibility = string.IsNullOrEmpty(ssh.ProtectedKeyPassphrase) ? Visibility.Collapsed : Visibility.Visible;
        FontSizeBox.Text = ssh.FontSize.ToString();
        TermTypeBox.Text = ssh.TerminalType;
        KeepAliveBox.Text = ssh.KeepAliveSeconds.ToString();

        if (entry.Protocol == ConnectionProtocol.Ssh) SshRadio.IsChecked = true;
        else RdpRadio.IsChecked = true;

        _initializing = false;
        UpdateProtocolVisibility();
        Loaded += (_, _) => (string.IsNullOrEmpty(HostBox.Text) ? HostBox : NameBox).Focus();
    }

    private ConnectionProtocol SelectedProtocol => SshRadio.IsChecked == true ? ConnectionProtocol.Ssh : ConnectionProtocol.Rdp;

    public static IEnumerable<GroupOption> BuildGroupOptions(IReadOnlyList<ConnectionGroup> groups, Guid? parentId, string prefix)
    {
        foreach (var g in groups.Where(g => g.ParentId == parentId).OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var label = prefix + g.Name;
            yield return new GroupOption(g.Id, label);
            foreach (var child in BuildGroupOptions(groups, g.Id, label + " / "))
                yield return child;
        }
    }

    private void OnProtocolChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        // Switch the port along with the protocol unless the user picked a custom port.
        var other = SelectedProtocol == ConnectionProtocol.Rdp ? ConnectionProtocol.Ssh : ConnectionProtocol.Rdp;
        if (!int.TryParse(PortBox.Text, out var port) || port == ConnectionEntry.DefaultPort(other))
            PortBox.Text = ConnectionEntry.DefaultPort(SelectedProtocol).ToString();

        UpdateProtocolVisibility();
    }

    private void OnScalingChanged(object sender, SelectionChangedEventArgs e) => UpdateProtocolVisibility();

    private void UpdateProtocolVisibility()
    {
        if (_initializing) return;
        var isRdp = SelectedProtocol == ConnectionProtocol.Rdp;
        RdpTabs.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;
        SshTabs.Visibility = isRdp ? Visibility.Collapsed : Visibility.Visible;
        DomainPanel.Visibility = isRdp ? Visibility.Visible : Visibility.Hidden;
        ResolutionPanel.Visibility = ScalingCombo.SelectedIndex == (int)RdpScalingMode.DynamicResolution
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Private key kiezen",
            Filter = "Private keys|*.*|PuTTY keys (*.ppk)|*.ppk",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        };
        if (dialog.ShowDialog(this) == true) KeyPathBox.Text = dialog.FileName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            ShowError("Vul een host of IP-adres in.");
            HostBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError("De poort moet een getal tussen 1 en 65535 zijn.");
            PortBox.Focus();
            return;
        }

        var protocol = SelectedProtocol;
        if (protocol == ConnectionProtocol.Ssh && string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ShowError("Voor SSH is een gebruikersnaam vereist.");
            UserBox.Focus();
            return;
        }

        _entry.Protocol = protocol;
        _entry.Name = NameBox.Text.Trim();
        _entry.Host = host;
        _entry.Port = port;
        _entry.Username = UserBox.Text.Trim();
        _entry.Domain = protocol == ConnectionProtocol.Rdp ? DomainBox.Text.Trim() : string.Empty;
        _entry.Notes = NotesBox.Text;
        _entry.GroupId = (GroupCombo.SelectedItem as GroupOption)?.Id;

        if (ClearPasswordCheck.IsChecked == true) _entry.ProtectedPassword = null;
        if (PasswordBox.Password.Length > 0) _entry.ProtectedPassword = CredentialProtector.Protect(PasswordBox.Password);

        var rdp = _entry.Rdp;
        rdp.ScalingMode = (RdpScalingMode)Math.Max(0, ScalingCombo.SelectedIndex);
        rdp.DesktopWidth = ParseInt(WidthBox.Text, rdp.DesktopWidth, 640, 8192);
        rdp.DesktopHeight = ParseInt(HeightBox.Text, rdp.DesktopHeight, 480, 8192);
        rdp.ScaleFactor = TagValue(ScaleCombo, 0);
        rdp.ColorDepth = TagValue(ColorCombo, 32);
        rdp.RedirectClipboard = ClipboardCheck.IsChecked == true;
        rdp.RedirectAudio = AudioCheck.IsChecked == true;
        rdp.RedirectDrives = DrivesCheck.IsChecked == true;
        rdp.RedirectPrinters = PrintersCheck.IsChecked == true;
        rdp.RedirectSmartCards = SmartCardCheck.IsChecked == true;
        rdp.UseNla = NlaCheck.IsChecked == true;
        rdp.AdminSession = AdminCheck.IsChecked == true;
        rdp.GatewayHost = GatewayBox.Text.Trim();
        rdp.GatewayUseSameCredentials = GatewaySameCredsCheck.IsChecked == true;

        var ssh = _entry.Ssh;
        ssh.AuthMode = (SshAuthMode)Math.Max(0, AuthCombo.SelectedIndex);
        ssh.PrivateKeyPath = KeyPathBox.Text.Trim().Trim('"');
        if (PassphraseBox.Password.Length > 0) ssh.ProtectedKeyPassphrase = CredentialProtector.Protect(PassphraseBox.Password);
        ssh.FontSize = ParseInt(FontSizeBox.Text, 14, 8, 32);
        ssh.TerminalType = string.IsNullOrWhiteSpace(TermTypeBox.Text) ? "xterm-256color" : TermTypeBox.Text.Trim();
        ssh.KeepAliveSeconds = ParseInt(KeepAliveBox.Text, 30, 0, 3600);

        if (ssh.AuthMode == SshAuthMode.PublicKey && protocol == ConnectionProtocol.Ssh && string.IsNullOrEmpty(ssh.PrivateKeyPath))
        {
            ShowError("Kies een private key voor public key authenticatie.");
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string message) => ErrorText.Text = message;

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private static void SelectByTag(ComboBox combo, int value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value.ToString())
                             ?? combo.Items[0];
    }

    private static int TagValue(ComboBox combo, int fallback)
        => combo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var v) ? v : fallback;
}
