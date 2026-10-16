using System.IO;
using System.Windows.Media.Imaging;

namespace Sendspin.Windows;

/// <summary>
/// Decodes album artwork no larger than it is shown.
/// </summary>
/// <remarks>
/// WPF decodes an image at full size unless told otherwise, so a 6000×6000 cover became a 144 MB
/// bitmap even when it was shown at a few hundred pixels, or blurred down to 100. The dimensions
/// are read from the header first, so an image already within the limit is decoded as it is
/// rather than scaled up.
/// </remarks>
internal static class ArtworkBitmap
{
    /// <summary>
    /// Longest side the artwork pane can need. The window is fixed at 410 DIPs wide, so this
    /// covers the pane at 300% display scaling.
    /// </summary>
    public const int DisplayMaxPixels = 1280;

    /// <summary>
    /// Decodes <paramref name="imageData"/> with its longer side at most <paramref name="maxPixels"/>.
    /// </summary>
    /// <param name="imageData">The encoded image.</param>
    /// <param name="maxPixels">Largest width or height to decode at, keeping the aspect ratio.</param>
    /// <returns>The frozen bitmap.</returns>
    public static BitmapImage Decode(byte[] imageData, int maxPixels)
    {
        int width, height;
        using (var probe = new MemoryStream(imageData))
        {
            // DelayCreation reads the header for the size without decoding any pixels.
            var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(imageData);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;

        // Setting only the longer side keeps the aspect ratio.
        if (width >= height && width > maxPixels)
        {
            bitmap.DecodePixelWidth = maxPixels;
        }
        else if (height > width && height > maxPixels)
        {
            bitmap.DecodePixelHeight = maxPixels;
        }

        bitmap.EndInit();
        bitmap.Freeze(); // Required for cross-thread access
        return bitmap;
    }
}
