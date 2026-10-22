using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConnectionHub.Models;
using ConnectionHub.Services;
using ConnectionHub.Ssh;
using ConnectionHub.ViewModels;
using Microsoft.Win32;
using Renci.SshNet.Common;

namespace ConnectionHub.Views;

public sealed class RemoteItemViewModel
{
    public RemoteItemViewModel(RemoteItem item) => Item = item;

    public RemoteItem Item { get; }
    public string Name => Item.Name;

    // E8B7 = Folder, E8A5 = Document
    public string Glyph => Item.IsDirectory ? "" : "";
    public Brush GlyphBrush => (Brush)Application.Current.FindResource(Item.IsDirectory ? "WarningBrush" : "SubTextBrush");
    public string SizeText => Item.IsDirectory ? string.Empty : FormatSize(Item.Size);
    public string ModifiedText => Item.Modified.ToString("g", CultureInfo.CurrentCulture);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>SFTP file browser tab for an SSH connection.</summary>
public partial class SftpView : UserControl, ISessionView
{
    private readonly ConnectionEntry _entry;
    private readonly ObservableCollection<RemoteItemViewModel> _items = new();
    private List<RemoteItem> _listing = new();
    private SftpSession? _session;
    private CancellationTokenSource? _transferCts;
    private string? _path;
    private bool _disposed;

    public SftpView(ConnectionEntry entry)
    {
        InitializeComponent();
        _entry = entry;
        FileList.ItemsSource = _items;
    }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public FrameworkElement Element => this;
    public bool SupportsFullScreen => false;

    public void Connect() => _ = ConnectAsync();

    public void Disconnect()
    {
        _transferCts?.Cancel();
        var session = _session;
        if (session == null) return;
        _session = null;
        session.Dispose();
        _items.Clear();
        ShowOverlay("", "Verbinding verbroken.");
        Report(SessionState.Disconnected, "Verbinding verbroken");
    }

    public void EnterFullScreen() { }

    public void FocusSession() => FileList.Focus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ---------- Connection ----------

    private async Task ConnectAsync()
    {
        Disconnect();

        var interaction = new DialogSshInteraction(Window.GetWindow(this));
        var password = CredentialProtector.Unprotect(_entry.ProtectedPassword);
        if (_entry.Ssh.AuthMode == SshAuthMode.Password && string.IsNullOrEmpty(password))
        {
            password = interaction.PromptSecret("Wachtwoord", $"Wachtwoord voor {_entry.Username}@{_entry.Host}");
            if (password == null)
            {
                ShowOverlay("", "Aanmelden geannuleerd.");
                Report(SessionState.Disconnected, "Aanmelden geannuleerd");
                return;
            }
        }

        var store = App.Store;
        var session = new SftpSession(new SshConnectOptions
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
        _session = session;

        ShowOverlay("", $"Verbinden met {_entry.Username}@{_entry.Host}…");
        Report(SessionState.Connecting, $"SFTP verbinden met {_entry.Host}…");
        try
        {
            await session.ConnectAsync();
            if (!ReferenceEquals(_session, session)) return;
            Report(SessionState.Connected, "SFTP verbonden");
            await NavigateAsync(_path ?? session.HomeDirectory);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            session.Dispose();
            var message = ex is SshAuthenticationException && _entry.Ssh.AuthMode == SshAuthMode.KeyboardInteractive
                ? $"Aanmelden mislukt: {ex.Message}\nOndersteunt de server geen keyboard-interactive, kies dan bij de verbinding 'Automatisch' of 'Wachtwoord'."
                : ex is OperationCanceledException ? "Aanmelden geannuleerd." : $"Verbinden mislukt: {ex.Message}";
            ShowOverlay("", message);
            Report(SessionState.Failed, message);
        }
    }

    private void Report(SessionState state, string message)
        => StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));

    // ---------- Navigation ----------

    private async Task NavigateAsync(string path)
    {
        if (_session is not { } session) return;
        try
        {
            SetStatus("Laden…");
            var listing = await session.ListAsync(path);
            _path = path;
            _listing = listing;
            PathBox.Text = path;
            ApplyListing();
        }
        catch (Exception ex)
        {
            HandleError(ex, $"Kan '{path}' niet openen");
            PathBox.Text = _path ?? string.Empty;
        }
    }

    private void ApplyListing()
    {
        var showHidden = HiddenCheck.IsChecked == true;
        _items.Clear();
        foreach (var item in _listing.Where(i => showHidden || !i.Name.StartsWith('.')))
            _items.Add(new RemoteItemViewModel(item));

        var hidden = _listing.Count - _items.Count;
        if (_items.Count == 0) ShowOverlay("", hidden > 0 ? "Deze map bevat alleen verborgen bestanden." : "Deze map is leeg.\nSleep bestanden hierheen om te uploaden.");
        else HideOverlay();

        var folders = _items.Count(i => i.Item.IsDirectory);
        SetStatus($"{folders} map(pen), {_items.Count - folders} bestand(en)" + (hidden > 0 && HiddenCheck.IsChecked != true ? $" · {hidden} verborgen" : string.Empty));
    }

    private Task RefreshAsync() => _path == null ? Task.CompletedTask : NavigateAsync(_path);

    private async Task OpenAsync(RemoteItemViewModel? vm)
    {
        if (vm == null || _session == null) return;
        var item = vm.Item;

        if (item.IsDirectory)
        {
            await NavigateAsync(item.FullName);
            return;
        }

        if (item.IsSymbolicLink)
        {
            // A link may point to a directory: try to open it as one first.
            try
            {
                await _session.ListAsync(item.FullName);
                await NavigateAsync(item.FullName);
                return;
            }
            catch (Exception)
            {
                // Not a directory; open it as a file below.
            }
        }

        // Download to a temporary folder and open with the default Windows application.
        var folder = Path.Combine(Path.GetTempPath(), "ConnectionHub", "sftp", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        if (await TransferAsync("Openen", (s, p, t) => s.DownloadAsync(new[] { item }, folder, p, t)))
        {
            var local = Directory.EnumerateFiles(folder).FirstOrDefault();
            if (local != null)
            {
                try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); }
                catch (Exception ex) { SetStatus($"Openen mislukt: {ex.Message}", error: true); }
            }
        }
    }

    // ---------- Transfers ----------

    /// <summary>Runs one transfer at a time with progress and cancel. Returns true on success.</summary>
    private async Task<bool> TransferAsync(string verb, Func<SftpSession, IProgress<TransferProgress>, CancellationToken, Task> run)
    {
        if (_session is not { } session) return false;
        if (_transferCts != null)
        {
            SetStatus("Er loopt al een overdracht. Wacht tot die klaar is of annuleer hem.", error: true);
            return false;
        }

        using var cts = new CancellationTokenSource();
        _transferCts = cts;
        TransferPanel.Visibility = Visibility.Visible;
        TransferProgressBar.Value = 0;
        TransferText.Text = $"{verb}…";

        var started = DateTime.UtcNow;
        var progress = new Progress<TransferProgress>(p =>
        {
            TransferProgressBar.Value = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
            var seconds = Math.Max(0.5, (DateTime.UtcNow - started).TotalSeconds);
            var speed = RemoteItemViewModel.FormatSize((long)(p.BytesDone / seconds));
            TransferText.Text = $"{verb}: {p.CurrentFile} ({Math.Min(p.FilesDone + 1, p.FilesTotal)}/{p.FilesTotal}) · " +
                                $"{RemoteItemViewModel.FormatSize(p.BytesDone)} van {RemoteItemViewModel.FormatSize(p.BytesTotal)} · {speed}/s";
        });

        try
        {
            await run(session, progress, cts.Token);
            SetStatus($"{verb} voltooid.");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{verb} geannuleerd.");
            return false;
        }
        catch (Exception ex)
        {
            HandleError(ex, $"{verb} mislukt");
            return false;
        }
        finally
        {
            _transferCts = null;
            TransferPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_path == null || localPaths.Count == 0) return;
        var target = _path;
        var existing = localPaths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/')))
            .Where(n => _listing.Any(i => i.Name == n)).ToList();
        if (existing.Count > 0 && !MessageWindow.Show(Window.GetWindow(this), "Overschrijven?",
                $"{existing.Count} item(s) bestaan al in {target}:\n{string.Join("\n", existing.Take(8))}{(existing.Count > 8 ? "\n…" : string.Empty)}\n\nBestaande bestanden worden overschreven.",
                MessageKind.Warning, "Overschrijven", "Annuleren"))
            return;

        await TransferAsync("Uploaden", (s, p, t) => s.UploadAsync(localPaths, target, p, t));
        if (_path == target) await RefreshAsync();
    }

    // ---------- UI handlers ----------

    private IReadOnlyList<RemoteItem> SelectedItems => FileList.SelectedItems.OfType<RemoteItemViewModel>().Select(v => v.Item).ToList();

    private async void OnUp(object sender, RoutedEventArgs e)
    {
        if (_path != null && _path != "/") await NavigateAsync(SftpSession.Parent(_path));
    }

    private async void OnHome(object sender, RoutedEventArgs e)
    {
        if (_session != null) await NavigateAsync(_session.HomeDirectory);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var path = PathBox.Text.Trim();
        if (path.Length == 0) return;
        if (!path.StartsWith('/') && _path != null) path = SftpSession.Combine(_path, path);
        await NavigateAsync(path);
    }

    private void OnHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (_session != null && _path != null) ApplyListing();
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem { DataContext: RemoteItemViewModel vm })
            await OpenAsync(vm);
    }

    private async void OnOpen(object sender, RoutedEventArgs e) => await OpenAsync(FileList.SelectedItem as RemoteItemViewModel);

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await OpenAsync(FileList.SelectedItem as RemoteItemViewModel);
                break;
            case Key.Back:
                e.Handled = true;
                OnUp(sender, e);
                break;
            case Key.F5:
                e.Handled = true;
                await RefreshAsync();
                break;
            case Key.F2:
                e.Handled = true;
                OnRename(sender, e);
                break;
            case Key.Delete:
                e.Handled = true;
                OnDelete(sender, e);
                break;
        }
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var dialog = new OpenFileDialog { Title = $"Uploaden naar {_path}", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await UploadAsync(dialog.FileNames);
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems;
        if (_session == null || items.Count == 0)
        {
            SetStatus("Selecteer eerst de bestanden of mappen die je wilt downloaden.");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Downloaden naar map" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var target = dialog.FolderName;
        var existing = items.Where(i => Path.Exists(Path.Combine(target, i.Name))).Select(i => i.Name).ToList();
        if (existing.Count > 0 && !MessageWindow.Show(Window.GetWindow(this), "Overschrijven?",
                $"{existing.Count} item(s) bestaan al in {target}:\n{string.Join("\n", existing.Take(8))}\n\nBestaande bestanden worden overschreven.",
                MessageKind.Warning, "Overschrijven", "Annuleren"))
            return;

        if (await TransferAsync("Downloaden", (s, p, t) => s.DownloadAsync(items, target, p, t)))
            SetStatus($"Gedownload naar {target}");
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (_session == null || _path == null) return;
        var name = AskName("Nieuwe map", "Naam van de nieuwe map:", string.Empty);
        if (name == null) return;
        try
        {
            await _session.CreateDirectoryAsync(SftpSession.Combine(_path, name));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HandleError(ex, "Map aanmaken mislukt");
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_session == null || FileList.SelectedItem is not RemoteItemViewModel vm) return;
        var name = AskName("Hernoemen", "Nieuwe naam:", vm.Name);
        if (name == null || name == vm.Name) return;
        try
        {
            await _session.RenameAsync(vm.Item, name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HandleError(ex, "Hernoemen mislukt");
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems;
        if (_session == null || items.Count == 0) return;

        var folders = items.Count(i => i.IsDirectory && !i.IsSymbolicLink);
        var message = items.Count == 1
            ? $"'{items[0].Name}' verwijderen?" + (folders == 1 ? "\n\nDe map wordt met alle inhoud verwijderd." : string.Empty)
            : $"{items.Count} items verwijderen?" + (folders > 0 ? $"\n\n{folders} map(pen) worden met alle inhoud verwijderd." : string.Empty);
        if (!MessageWindow.Show(Window.GetWindow(this), "Verwijderen", message + "\nDit kan niet ongedaan worden gemaakt.",
                MessageKind.Warning, "Verwijderen", "Annuleren"))
            return;

        if (await TransferAsync("Verwijderen", (s, _, t) => s.DeleteAsync(items, t)))
            await RefreshAsync();
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var text = FileList.SelectedItem is RemoteItemViewModel vm ? vm.Item.FullName : _path;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); SetStatus($"Gekopieerd: {text}"); }
        catch (Exception) { /* clipboard busy */ }
    }

    private void OnCancelTransfer(object sender, RoutedEventArgs e) => _transferCts?.Cancel();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _session != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_session != null && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await UploadAsync(paths);
    }

    // ---------- Helpers ----------

    private string? AskName(string title, string label, string initial)
    {
        while (true)
        {
            var dialog = new PromptWindow(_path ?? string.Empty, title, string.Empty, new[] { new AuthPrompt(label, true) }, initial)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true) return null;
            var name = dialog.Answers[0].Trim();
            if (name.Length > 0 && !name.Contains('/') && name is not "." and not "..") return name;
            MessageWindow.Show(Window.GetWindow(this), title, "Ongeldige naam. Een naam mag niet leeg zijn en geen '/' bevatten.", MessageKind.Info, "OK");
            initial = name;
        }
    }

    private void HandleError(Exception ex, string context)
    {
        var detail = ex switch
        {
            SftpPermissionDeniedException => "geen toegang (onvoldoende rechten)",
            SftpPathNotFoundException => "bestand of map bestaat niet (meer)",
            _ => ex.Message
        };
        SetStatus($"{context}: {detail}", error: true);

        if (_session is { IsConnected: false })
        {
            var session = _session;
            _session = null;
            session.Dispose();
            ShowOverlay("", "De SFTP-verbinding is verbroken.");
            Report(SessionState.Failed, $"SFTP-verbinding verbroken: {ex.Message}");
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "SubTextBrush");
    }

    private void ShowOverlay(string glyph, string text)
    {
        OverlayIcon.Text = glyph;
        OverlayText.Text = text;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;
}
