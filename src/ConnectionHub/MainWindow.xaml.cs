using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConnectionHub.Services;
using ConnectionHub.ViewModels;
using ConnectionHub.Views;

namespace ConnectionHub;

public partial class MainWindow : Window
{
    private const string DragFormat = "ConnectionHub.TreeItem";

    private readonly MainViewModel _vm;
    private Point _dragStart;
    private TreeItemViewModel? _dragItem;
    private TreeItemViewModel? _clickItem;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheming.Attach(this);
        _vm = new MainViewModel(App.Store);
        DataContext = _vm;

        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.F, ModifierKeys.Control));
        Loaded += (_, _) => QuickConnectBox.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var active = _vm.Sessions.Count(s => s.State is SessionState.Connected or SessionState.Connecting);
        if (active > 0 && !MessageWindow.Show(this, "Connection Hub afsluiten",
                $"Er zijn nog {active} actieve sessie(s). Deze worden verbroken.", MessageKind.Warning, "Afsluiten", "Annuleren"))
        {
            e.Cancel = true;
            return;
        }

        _vm.CloseAllSessions();
        try { App.Store.Save(); }
        catch (Exception) { /* nothing sensible left to do while closing */ }
        base.OnClosing(e);
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = string.Empty;
        SearchBox.Focus();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Tree.ContextMenu is { } menu)
        {
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = _vm;
            menu.IsOpen = true;
        }
    }

    private void OnTabsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    // ---------- Tree interaction ----------

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _vm.SelectedItem = e.NewValue as TreeItemViewModel;

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromElement(e.OriginalSource as DependencyObject) is ConnectionItemViewModel item)
        {
            _vm.Connect(item);
            e.Handled = true;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        var item = _vm.SelectedItem;
        if (item == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                _vm.ConnectCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.F2:
                _vm.EditCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Delete:
                _vm.DeleteCommand.Execute(item);
                e.Handled = true;
                break;
        }
    }

    private void OnTreePreviewRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Select the item under the cursor so context menu actions apply to it.
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } tvi)
            tvi.IsSelected = true;
    }

    private void OnTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragItem = ItemFromElement(e.OriginalSource as DependencyObject);
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) != null)
            _dragItem = null;
        _clickItem = _dragItem;
    }

    private void OnTreePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // A single click on a connection that already has an open session switches to that session.
        var item = ItemFromElement(e.OriginalSource as DependencyObject);
        if (item != null && ReferenceEquals(item, _clickItem))
            _vm.ActivateExistingSession(item);
        _clickItem = null;
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;

        var pos = e.GetPosition(Tree);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;
        _clickItem = null;
        DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, item), DragDropEffects.Move);
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DragFormat) is TreeItemViewModel dragged)
        {
            var target = DropTarget(e);
            var valid = target == null || (!ReferenceEquals(target, dragged) && !target.IsDescendantOf(dragged));
            if (valid) e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not TreeItemViewModel dragged) return;
        var target = DropTarget(e);
        if (target != null && (ReferenceEquals(target, dragged) || target.IsDescendantOf(dragged))) return;
        _vm.Move(dragged, target);
        e.Handled = true;
    }

    /// <summary>The group an item is dropped into: the group under the cursor, the parent of a connection, or null (top level).</summary>
    private static GroupItemViewModel? DropTarget(DragEventArgs e) => ItemFromElement(e.OriginalSource as DependencyObject) switch
    {
        GroupItemViewModel g => g,
        ConnectionItemViewModel c => c.Parent,
        _ => null
    };

    private static TreeItemViewModel? ItemFromElement(DependencyObject? source)
        => FindAncestor<TreeViewItem>(source)?.DataContext as TreeItemViewModel;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
