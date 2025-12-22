using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SendSpinClient.Resources.Converters;

/// <summary>
/// Converts a boolean to Visibility.
/// Use ConverterParameter="inverse" to invert the logic.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        var inverse = parameter?.ToString()?.ToLowerInvariant() == "inverse";

        if (inverse) boolValue = !boolValue;

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts null to Visibility.Collapsed, non-null to Visible.
/// Use ConverterParameter="inverse" to invert the logic.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string str || !string.IsNullOrEmpty(str));
        var inverse = parameter?.ToString()?.ToLowerInvariant() == "inverse";

        if (inverse) hasValue = !hasValue;

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts count to Visibility. 0 = Visible (for empty state), >0 = Collapsed.
/// Use ConverterParameter="inverse" to invert.
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var inverse = parameter?.ToString()?.ToLowerInvariant() == "inverse";

        // Default: show when count is 0 (empty state)
        var show = count == 0;
        if (inverse) show = !show;

        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts IsPlaying boolean to play/pause icon only.
/// Used for main window buttons.
/// </summary>
public class PlayPauseIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isPlaying = value is bool b && b;
        return isPlaying ? "⏸" : "▶";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts IsPlaying boolean to play/pause text with icon.
/// Used for tray context menu.
/// </summary>
public class PlayPauseTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isPlaying = value is bool b && b;
        return isPlaying ? "⏸ Pause" : "▶ Play";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts IsConnected to connect button text.
/// </summary>
public class ConnectButtonTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isConnected = value is bool b && b;
        return isConnected ? "Disconnect" : "Connect";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts IsMuted boolean to mute/unmute icon only.
/// Used for main window buttons.
/// </summary>
public class MuteIconOnlyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isMuted = value is bool b && b;
        return isMuted ? "🔇" : "🔊";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts IsMuted boolean to mute/unmute text with icon.
/// Used for tray context menu.
/// </summary>
public class MuteIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isMuted = value is bool b && b;
        return isMuted ? "🔇 Unmute" : "🔊 Mute";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Inverts a boolean value. Used for IsEnabled bindings.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }
}
