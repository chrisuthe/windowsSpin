using System.Globalization;
using System.Windows.Data;

namespace Sendspin.Windows;

/// <summary>
/// Converts a byte array containing image data to a BitmapImage for display.
/// Used for album artwork received from the Sendspin server.
/// </summary>
public class ByteArrayToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] imageData || imageData.Length == 0)
            return null;

        try
        {
            return ArtworkBitmap.Decode(imageData, ArtworkBitmap.DisplayMaxPixels);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
