using System.Windows;
using System.Windows.Media;
using ConnectionHub.Services;

namespace ConnectionHub.Views;

public enum MessageKind
{
    Info,
    Question,
    Warning,
    Error
}

public partial class MessageWindow : Window
{
    private MessageWindow()
    {
        InitializeComponent();
        WindowTheming.Attach(this);
    }

    /// <summary>Shows a themed message box. Returns true when the primary button was chosen.</summary>
    public static bool Show(Window? owner, string header, string message, MessageKind kind, string primary, string? secondary = null)
    {
        var w = new MessageWindow();
        if (owner is { IsLoaded: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        w.HeaderText.Text = header;
        w.MessageText.Text = message;
        w.PrimaryButton.Content = primary;
        if (secondary == null)
        {
            w.SecondaryButton.Visibility = Visibility.Collapsed;
            w.PrimaryButton.IsCancel = true;
        }
        else
        {
            w.SecondaryButton.Content = secondary;
        }

        var (glyph, brushKey) = kind switch
        {
            MessageKind.Question => ("\uE9CE", "AccentHoverBrush"),
            MessageKind.Warning => ("\uE7BA", "WarningBrush"),
            MessageKind.Error => ("\uEA39", "DangerBrush"),
            _ => ("\uE946", "AccentHoverBrush")
        };
        var brush = (SolidColorBrush)w.FindResource(brushKey);
        w.IconGlyph.Text = glyph;
        w.IconGlyph.Foreground = brush;
        w.IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, brush.Color.R, brush.Color.G, brush.Color.B));
        if (kind is MessageKind.Warning or MessageKind.Error && secondary != null)
            w.PrimaryButton.Style = (Style)w.FindResource("DangerButton");

        return w.ShowDialog() == true;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnSecondary(object sender, RoutedEventArgs e) => DialogResult = false;
}
