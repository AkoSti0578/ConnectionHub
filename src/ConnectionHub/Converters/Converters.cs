using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ConnectionHub.Models;
using ConnectionHub.ViewModels;

namespace ConnectionHub.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Collapses null, empty strings and zero. ConverterParameter "inverse" flips the result.</summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value == null || value is string { Length: 0 } || value is int and 0;
        if (parameter is "inverse") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SessionStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is SessionState state
            ? state switch
            {
                SessionState.Connected => "SuccessBrush",
                SessionState.Connecting => "WarningBrush",
                SessionState.Failed => "DangerBrush",
                _ => "MutedTextBrush"
            }
            : "MutedTextBrush";
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows the session banner when the session is not connected.</summary>
public sealed class SessionStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SessionState.Disconnected or SessionState.Failed ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProtocolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Application.Current.FindResource(value is ConnectionProtocol.Ssh ? "SshBrush" : "RdpBrush");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible for RDP connections; with <see cref="Invert"/> visible for SSH connections.</summary>
public sealed class RdpOnlyVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ConnectionProtocol protocol && (protocol == ConnectionProtocol.Rdp) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProtocolToGlyphConverter : IValueConverter
{
    // E756 = CommandPrompt, E7F4 = TVMonitor
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ConnectionProtocol.Ssh ? "\uE756" : "\uE7F4";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string s && ColorConverter.ConvertFromString(s) is Color c)
                return new SolidColorBrush(c);
        }
        catch (FormatException)
        {
        }
        return Application.Current.FindResource("AccentBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
