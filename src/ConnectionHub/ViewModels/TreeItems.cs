using System;
using System.Collections.ObjectModel;
using ConnectionHub.Models;

namespace ConnectionHub.ViewModels;

public abstract class TreeItemViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;
    private bool _isVisible = true;

    public GroupItemViewModel? Parent { get; set; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = new();

    public abstract string Name { get; }
    public abstract Guid Id { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value)) OnExpandedChanged(value);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>False when hidden by the search filter.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    protected virtual void OnExpandedChanged(bool expanded) { }

    public bool IsDescendantOf(TreeItemViewModel other)
    {
        for (var p = Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, other)) return true;
        return false;
    }
}

public sealed class GroupItemViewModel : TreeItemViewModel
{
    public GroupItemViewModel(ConnectionGroup model)
    {
        Model = model;
        IsExpanded = model.IsExpanded;
    }

    public ConnectionGroup Model { get; }
    public override string Name => Model.Name;
    public override Guid Id => Model.Id;
    public string Color => Model.Color;

    public int ConnectionCount
    {
        get
        {
            var count = 0;
            foreach (var child in Children)
                count += child is GroupItemViewModel g ? g.ConnectionCount : 1;
            return count;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(ConnectionCount));
    }

    protected override void OnExpandedChanged(bool expanded) => Model.IsExpanded = expanded;
}

public sealed class ConnectionItemViewModel : TreeItemViewModel
{
    private bool _isOpen;

    public ConnectionItemViewModel(ConnectionEntry model) => Model = model;

    public ConnectionEntry Model { get; }
    public override string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.Host : Model.Name;
    public override Guid Id => Model.Id;
    public ConnectionProtocol Protocol => Model.Protocol;
    public string ProtocolLabel => Model.Protocol == ConnectionProtocol.Rdp ? "RDP" : "SSH";
    public string? NotesToolTip => string.IsNullOrWhiteSpace(Model.Notes) ? null : Model.Notes;

    public string Subtitle
    {
        get
        {
            var user = string.IsNullOrEmpty(Model.Username) ? string.Empty : Model.Username + "@";
            var port = Model.Port == ConnectionEntry.DefaultPort(Model.Protocol) ? string.Empty : ":" + Model.Port;
            return user + Model.Host + port;
        }
    }

    /// <summary>True while a session for this connection is open.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(ProtocolLabel));
    }
}
