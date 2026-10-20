using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConnectionHub.Models;
using ConnectionHub.Services;

namespace ConnectionHub.Views;

public partial class GroupEditorWindow : Window
{
    private static readonly string[] Palette =
    {
        "#6366F1", "#0EA5E9", "#14B8A6", "#22C55E", "#EAB308", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#64748B"
    };

    private readonly ConnectionGroup _group;
    private string _color;

    public GroupEditorWindow(ConnectionGroup group, bool isNew, IReadOnlyList<ConnectionGroup> groups)
    {
        InitializeComponent();
        WindowTheming.Attach(this);
        _group = group;
        _color = group.Color;

        HeaderText.Text = isNew ? "Nieuwe groep" : "Groep bewerken";
        Title = HeaderText.Text;
        NameBox.Text = group.Name;

        // A group cannot be moved into itself or one of its own subgroups.
        var excluded = new HashSet<Guid> { group.Id };
        bool added;
        do
        {
            added = false;
            foreach (var g in groups.Where(g => g.ParentId is { } p && excluded.Contains(p) && !excluded.Contains(g.Id)))
                added |= excluded.Add(g.Id);
        } while (added);

        var options = new List<GroupOption> { new(null, "(hoofdniveau)") };
        options.AddRange(ConnectionEditorWindow.BuildGroupOptions(groups, null, string.Empty).Where(o => o.Id is not { } id || !excluded.Contains(id)));
        ParentCombo.ItemsSource = options;
        ParentCombo.SelectedItem = options.FirstOrDefault(o => o.Id == group.ParentId) ?? options[0];

        var colors = Palette.Contains(_color, StringComparer.OrdinalIgnoreCase) ? Palette : Palette.Append(_color).ToArray();
        ColorList.ItemsSource = colors;
        ColorList.Loaded += (_, _) => CheckCurrentColor();

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void CheckCurrentColor()
    {
        foreach (var item in ColorList.Items)
        {
            if (ColorList.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter cp
                && VisualTreeHelper.GetChildrenCount(cp) > 0
                && VisualTreeHelper.GetChild(cp, 0) is RadioButton rb)
            {
                rb.IsChecked = string.Equals((string)item, _color, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private void OnColorChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string color }) _color = color;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "Geef de groep een naam.";
            NameBox.Focus();
            return;
        }

        _group.Name = name;
        _group.ParentId = (ParentCombo.SelectedItem as GroupOption)?.Id;
        _group.Color = _color;
        DialogResult = true;
    }
}
