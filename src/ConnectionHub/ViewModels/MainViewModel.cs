using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using ConnectionHub.Models;
using ConnectionHub.Services;
using ConnectionHub.Views;
using Microsoft.Win32;

namespace ConnectionHub.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly Regex QuickConnectPattern = new(
        @"^\s*(?:(?<proto>ssh|rdp)(?:://|\s+))?(?:(?<user>[^@\s]+)@)?(?<host>\[[^\]]+\]|[^\s:]+)(?::(?<port>\d{1,5}))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConnectionStore _store;
    private SessionViewModel? _activeSession;
    private TreeItemViewModel? _selectedItem;
    private string _searchText = string.Empty;
    private string _quickConnectText = string.Empty;

    public MainViewModel(ConnectionStore store)
    {
        _store = store;

        NewConnectionCommand = new RelayCommand(p => NewConnection(TargetGroup(p)));
        NewGroupCommand = new RelayCommand(p => NewGroup(TargetGroup(p)));
        ConnectCommand = new RelayCommand(p => Connect(ItemFrom(p)));
        ConnectExternalCommand = new RelayCommand(p => ConnectExternal(ItemFrom(p), false));
        ConnectExternalMultiMonitorCommand = new RelayCommand(p => ConnectExternal(ItemFrom(p), true));
        EditCommand = new RelayCommand(p => Edit(ItemFrom(p)));
        DuplicateCommand = new RelayCommand(p => Duplicate(ItemFrom(p)));
        DeleteCommand = new RelayCommand(p => Delete(ItemFrom(p)));
        ConnectGroupCommand = new RelayCommand(p => ConnectGroup(ItemFrom(p) as GroupItemViewModel));
        CloseSessionCommand = new RelayCommand(p => CloseSession(p as SessionViewModel ?? ActiveSession));
        ActivateSessionCommand = new RelayCommand(p => ActiveSession = p as SessionViewModel);
        NextSessionCommand = new RelayCommand(() => CycleSession(1));
        PreviousSessionCommand = new RelayCommand(() => CycleSession(-1));
        QuickConnectCommand = new RelayCommand(QuickConnect);
        ExportCommand = new RelayCommand(Export);
        ImportCommand = new RelayCommand(Import);
        ExpandAllCommand = new RelayCommand(() => SetExpanded(RootItems, true));
        CollapseAllCommand = new RelayCommand(() => SetExpanded(RootItems, false));
        ShowHomeCommand = new RelayCommand(() => ActiveSession = null);

        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(ShowDashboard));
        };
        RebuildTree();
    }

    public ObservableCollection<TreeItemViewModel> RootItems { get; } = new();
    public ObservableCollection<SessionViewModel> Sessions { get; } = new();
    public ObservableCollection<ConnectionItemViewModel> RecentConnections { get; } = new();

    public ICommand NewConnectionCommand { get; }
    public ICommand NewGroupCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectExternalCommand { get; }
    public ICommand ConnectExternalMultiMonitorCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConnectGroupCommand { get; }
    public ICommand CloseSessionCommand { get; }
    public ICommand ActivateSessionCommand { get; }
    public ICommand NextSessionCommand { get; }
    public ICommand PreviousSessionCommand { get; }
    public ICommand QuickConnectCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ShowHomeCommand { get; }

    public bool HasSessions => Sessions.Count > 0;
    public bool ShowDashboard => ActiveSession == null;
    public int ConnectionCount => _store.Data.Connections.Count;
    public int GroupCount => _store.Data.Groups.Count;
    public string DataLocation => _store.FilePath;

    public SessionViewModel? ActiveSession
    {
        get => _activeSession;
        set
        {
            var previous = _activeSession;
            if (!SetProperty(ref _activeSession, value)) return;
            if (previous != null) previous.IsActive = false;
            if (value != null) value.IsActive = true;
            OnPropertyChanged(nameof(ShowDashboard));
        }
    }

    public TreeItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    public string QuickConnectText
    {
        get => _quickConnectText;
        set => SetProperty(ref _quickConnectText, value);
    }

    private static Window? Owner => Application.Current.MainWindow;

    // ---------- Tree ----------

    public void RebuildTree()
    {
        var expanded = new Dictionary<Guid, bool>();
        var selectedId = SelectedItem?.Id;

        RootItems.Clear();
        var groups = _store.Data.Groups.ToDictionary(g => g.Id, g => new GroupItemViewModel(g));

        foreach (var vm in groups.Values.OrderBy(g => g.Model.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (vm.Model.ParentId is { } parentId && groups.TryGetValue(parentId, out var parent))
            {
                vm.Parent = parent;
                parent.Children.Add(vm);
            }
            else
            {
                vm.Model.ParentId = null;
                RootItems.Add(vm);
            }
        }

        foreach (var c in _store.Data.Connections.OrderBy(c => string.IsNullOrWhiteSpace(c.Name) ? c.Host : c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var vm = new ConnectionItemViewModel(c)
            {
                IsOpen = Sessions.Any(s => s.Entry.Id == c.Id)
            };
            if (c.GroupId is { } gid && groups.TryGetValue(gid, out var group))
            {
                vm.Parent = group;
                group.Children.Add(vm);
            }
            else
            {
                c.GroupId = null;
                RootItems.Add(vm);
            }
        }

        foreach (var g in groups.Values) g.Refresh();
        if (selectedId is { } id && FindItem(id) is { } sel) sel.IsSelected = true;

        RefreshRecent();
        ApplyFilter();
        OnPropertyChanged(nameof(ConnectionCount));
        OnPropertyChanged(nameof(GroupCount));
    }

    private void RefreshRecent()
    {
        RecentConnections.Clear();
        foreach (var c in _store.Data.Connections
                     .OrderByDescending(c => c.LastConnected ?? DateTime.MinValue)
                     .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(8))
        {
            RecentConnections.Add(new ConnectionItemViewModel(c));
        }
    }

    private TreeItemViewModel? FindItem(Guid id) => Flatten(RootItems).FirstOrDefault(i => i.Id == id);

    private static IEnumerable<TreeItemViewModel> Flatten(IEnumerable<TreeItemViewModel> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        foreach (var item in RootItems) Filter(item, term);
    }

    private static bool Filter(TreeItemViewModel item, string term)
    {
        if (term.Length == 0)
        {
            foreach (var child in item.Children) Filter(child, term);
            item.IsVisible = true;
            return true;
        }

        var selfMatch = item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                        || item is ConnectionItemViewModel c
                        && (c.Model.Host.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                            || c.Model.Username.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                            || c.ProtocolLabel.Equals(term, StringComparison.OrdinalIgnoreCase));

        var childMatch = false;
        foreach (var child in item.Children)
        {
            // Children of a matching group stay visible.
            childMatch |= Filter(child, selfMatch ? string.Empty : term);
        }

        item.IsVisible = selfMatch || childMatch;
        if (childMatch && !selfMatch) item.IsExpanded = true;
        return item.IsVisible;
    }

    private static void SetExpanded(IEnumerable<TreeItemViewModel> items, bool expanded)
    {
        foreach (var item in items)
        {
            if (item is GroupItemViewModel) item.IsExpanded = expanded;
            SetExpanded(item.Children, expanded);
        }
    }

    private TreeItemViewModel? ItemFrom(object? parameter) => parameter as TreeItemViewModel ?? SelectedItem;

    private Guid? TargetGroup(object? parameter) => ItemFrom(parameter) switch
    {
        GroupItemViewModel g => g.Id,
        ConnectionItemViewModel c => c.Model.GroupId,
        _ => null
    };

    // ---------- CRUD ----------

    public void NewConnection(Guid? groupId)
    {
        var entry = new ConnectionEntry { GroupId = groupId };
        var editor = new ConnectionEditorWindow(entry, true, _store.Data.Groups) { Owner = Owner };
        if (editor.ShowDialog() != true) return;

        _store.Data.Connections.Add(entry);
        Save();
        RebuildTree();
        if (FindItem(entry.Id) is { } item) item.IsSelected = true;
    }

    public void NewGroup(Guid? parentId)
    {
        var group = new ConnectionGroup
        {
            ParentId = parentId,
            SortOrder = _store.Data.Groups.Count
        };
        var editor = new GroupEditorWindow(group, true, _store.Data.Groups) { Owner = Owner };
        if (editor.ShowDialog() != true) return;

        _store.Data.Groups.Add(group);
        Save();
        RebuildTree();
    }

    private void Edit(TreeItemViewModel? item)
    {
        switch (item)
        {
            case ConnectionItemViewModel c:
            {
                var editor = new ConnectionEditorWindow(c.Model, false, _store.Data.Groups) { Owner = Owner };
                if (editor.ShowDialog() != true) return;
                break;
            }
            case GroupItemViewModel g:
            {
                var editor = new GroupEditorWindow(g.Model, false, _store.Data.Groups) { Owner = Owner };
                if (editor.ShowDialog() != true) return;
                break;
            }
            default:
                return;
        }
        Save();
        RebuildTree();
    }

    private void Duplicate(TreeItemViewModel? item)
    {
        if (item is not ConnectionItemViewModel c) return;
        var json = System.Text.Json.JsonSerializer.Serialize(c.Model);
        var copy = System.Text.Json.JsonSerializer.Deserialize<ConnectionEntry>(json)!;
        copy.Id = Guid.NewGuid();
        copy.Name = c.Name + " (kopie)";
        copy.LastConnected = null;
        _store.Data.Connections.Add(copy);
        Save();
        RebuildTree();
        if (FindItem(copy.Id) is { } vm) vm.IsSelected = true;
    }

    private void Delete(TreeItemViewModel? item)
    {
        switch (item)
        {
            case ConnectionItemViewModel c:
                if (!MessageWindow.Show(Owner, "Verbinding verwijderen", $"Weet je zeker dat je '{c.Name}' wilt verwijderen?",
                        MessageKind.Warning, "Verwijderen", "Annuleren")) return;
                _store.Data.Connections.RemoveAll(x => x.Id == c.Id);
                break;

            case GroupItemViewModel g:
            {
                var groupIds = Flatten(new[] { g }).OfType<GroupItemViewModel>().Select(x => x.Id).ToHashSet();
                var connections = _store.Data.Connections.Where(x => x.GroupId is { } id && groupIds.Contains(id)).ToList();
                var message = connections.Count == 0
                    ? $"Groep '{g.Name}' verwijderen?"
                    : $"Groep '{g.Name}' bevat {connections.Count} verbinding(en). Deze worden verplaatst naar " +
                      (g.Model.ParentId == null ? "het hoofdniveau." : "de bovenliggende groep.");
                if (!MessageWindow.Show(Owner, "Groep verwijderen", message, MessageKind.Warning, "Verwijderen", "Annuleren")) return;

                foreach (var conn in connections) conn.GroupId = g.Model.ParentId;
                _store.Data.Groups.RemoveAll(x => groupIds.Contains(x.Id));
                break;
            }
            default:
                return;
        }
        Save();
        RebuildTree();
    }

    /// <summary>Moves a connection or group into a target group (null = top level). Used by drag and drop.</summary>
    public void Move(TreeItemViewModel item, GroupItemViewModel? target)
    {
        if (ReferenceEquals(item, target)) return;
        switch (item)
        {
            case ConnectionItemViewModel c:
                if (c.Model.GroupId == target?.Id) return;
                c.Model.GroupId = target?.Id;
                break;
            case GroupItemViewModel g:
                if (target != null && (target.IsDescendantOf(g) || g.Model.ParentId == target.Id)) return;
                if (target == null && g.Model.ParentId == null) return;
                g.Model.ParentId = target?.Id;
                break;
        }
        if (target != null) target.IsExpanded = true;
        Save();
        RebuildTree();
    }

    private void Save()
    {
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            MessageWindow.Show(Owner, "Opslaan mislukt", ex.Message, MessageKind.Error, "OK");
        }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Verbindingen exporteren",
            Filter = "Connection Hub (*.json)|*.json",
            FileName = $"connectionhub-export-{DateTime.Now:yyyyMMdd}.json"
        };
        if (dialog.ShowDialog(Owner) != true) return;

        var includePasswords = MessageWindow.Show(Owner, "Wachtwoorden meenemen?",
            "Opgeslagen wachtwoorden zijn versleuteld met je Windows-account en werken alleen voor jou op deze computer.\n\nWil je ze toch meenemen in de export?",
            MessageKind.Question, "Ja, meenemen", "Nee");
        try
        {
            _store.Export(dialog.FileName, includePasswords);
        }
        catch (Exception ex)
        {
            MessageWindow.Show(Owner, "Export mislukt", ex.Message, MessageKind.Error, "OK");
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Verbindingen importeren", Filter = "Connection Hub (*.json)|*.json" };
        if (dialog.ShowDialog(Owner) != true) return;
        try
        {
            var count = _store.Import(dialog.FileName);
            Save();
            RebuildTree();
            MessageWindow.Show(Owner, "Import voltooid", $"{count} verbinding(en) geïmporteerd.", MessageKind.Info, "OK");
        }
        catch (Exception ex)
        {
            MessageWindow.Show(Owner, "Import mislukt", ex.Message, MessageKind.Error, "OK");
        }
    }

    // ---------- Sessions ----------

    public void Connect(TreeItemViewModel? item)
    {
        switch (item)
        {
            case ConnectionItemViewModel c:
                OpenSession(c.Model);
                break;
            case GroupItemViewModel g:
                item.IsExpanded = !item.IsExpanded;
                break;
        }
    }

    private void ConnectGroup(GroupItemViewModel? group)
    {
        if (group == null) return;
        var connections = Flatten(group.Children).OfType<ConnectionItemViewModel>().ToList();
        if (connections.Count > 8 && !MessageWindow.Show(Owner, "Alle verbindingen openen",
                $"Er worden {connections.Count} sessies geopend. Doorgaan?", MessageKind.Question, "Openen", "Annuleren"))
            return;
        foreach (var c in connections) OpenSession(c.Model, activate: false);
        if (Sessions.LastOrDefault() is { } last) ActiveSession = last;
    }

    private void ConnectExternal(TreeItemViewModel? item, bool multiMonitor)
    {
        if (item is not ConnectionItemViewModel { Protocol: ConnectionProtocol.Rdp } c) return;
        try
        {
            ExternalRdpLauncher.Launch(c.Model, multiMonitor);
            c.Model.LastConnected = DateTime.Now;
            Save();
            RefreshRecent();
        }
        catch (Exception ex)
        {
            MessageWindow.Show(Owner, "Starten mislukt", ex.Message, MessageKind.Error, "OK");
        }
    }

    public void OpenSession(ConnectionEntry entry, bool activate = true)
    {
        ISessionView view = entry.Protocol == ConnectionProtocol.Rdp ? new RdpSessionView(entry) : new TerminalView(entry);
        var session = new SessionViewModel(entry, view);
        Sessions.Add(session);
        if (activate || ActiveSession == null) ActiveSession = session;
        session.Start();
        UpdateOpenState();
        RefreshRecent();
    }

    public void CloseSession(SessionViewModel? session)
    {
        if (session == null) return;
        var index = Sessions.IndexOf(session);
        if (index < 0) return;

        if (ReferenceEquals(ActiveSession, session))
            ActiveSession = Sessions.Count > 1 ? Sessions[index == Sessions.Count - 1 ? index - 1 : index + 1] : null;

        Sessions.Remove(session);
        session.Dispose();
        UpdateOpenState();
    }

    public void CloseAllSessions()
    {
        foreach (var s in Sessions.ToList())
        {
            Sessions.Remove(s);
            s.Dispose();
        }
        ActiveSession = null;
    }

    private void CycleSession(int direction)
    {
        if (Sessions.Count == 0) return;
        var index = ActiveSession == null ? -1 : Sessions.IndexOf(ActiveSession);
        index = (index + direction + Sessions.Count) % Sessions.Count;
        ActiveSession = Sessions[index];
    }

    private void UpdateOpenState()
    {
        var open = Sessions.Select(s => s.Entry.Id).ToHashSet();
        foreach (var c in Flatten(RootItems).OfType<ConnectionItemViewModel>())
            c.IsOpen = open.Contains(c.Id);
    }

    /// <summary>Accepts "host", "user@host", "ssh user@host:2222", "rdp://host:3390".</summary>
    private void QuickConnect()
    {
        var match = QuickConnectPattern.Match(QuickConnectText);
        if (!match.Success)
        {
            MessageWindow.Show(Owner, "Snel verbinden", "Gebruik bijvoorbeeld:\n  rdp server01\n  ssh beheer@10.0.0.5\n  ssh beheer@host:2222",
                MessageKind.Info, "OK");
            return;
        }

        var user = match.Groups["user"].Success ? match.Groups["user"].Value : string.Empty;
        var protocol = match.Groups["proto"].Success
            ? match.Groups["proto"].Value.Equals("ssh", StringComparison.OrdinalIgnoreCase) ? ConnectionProtocol.Ssh : ConnectionProtocol.Rdp
            : user.Length > 0 ? ConnectionProtocol.Ssh : ConnectionProtocol.Rdp;
        var host = match.Groups["host"].Value.Trim('[', ']');
        var port = match.Groups["port"].Success && int.TryParse(match.Groups["port"].Value, out var p) && p is > 0 and < 65536
            ? p
            : ConnectionEntry.DefaultPort(protocol);

        if (protocol == ConnectionProtocol.Ssh && user.Length == 0)
        {
            var dialog = new PromptWindow(host, "Gebruikersnaam", string.Empty, new[] { new Ssh.AuthPrompt("Gebruikersnaam:", true) }) { Owner = Owner };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Answers[0])) return;
            user = dialog.Answers[0].Trim();
        }

        // Reuse a saved connection to the same target when there is one.
        var existing = _store.Data.Connections.FirstOrDefault(c => c.Protocol == protocol && c.Port == port
            && string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
            && (user.Length == 0 || string.Equals(c.Username, user, StringComparison.OrdinalIgnoreCase)));

        OpenSession(existing ?? new ConnectionEntry
        {
            Name = host,
            Protocol = protocol,
            Host = host,
            Port = port,
            Username = user
        });
        QuickConnectText = string.Empty;
    }
}
