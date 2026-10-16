using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Sendspin.Windows;

/// <summary>
/// Converts a byte array containing image data to a blurred BitmapSource for background display.
/// Optimized for performance by scaling down the image before blurring.
/// </summary>
public class BlurredImageConverter : IValueConverter
{
    private const int ScaledSize = 100;
    private const double BlurRadius = 30;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] imageData || imageData.Length == 0)
            return null;

        try
        {
            // Decode straight at the small size for performance (blur on small image, then stretch)
            var scaled = ArtworkBitmap.Decode(imageData, ScaledSize);

            // Apply blur effect and render to bitmap
            var blurred = ApplyBlur(scaled, BlurRadius);
            blurred.Freeze(); // Required for cross-thread access

            return blurred;
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

    private static BitmapSource ApplyBlur(BitmapSource source, double radius)
    {
        // Create a DrawingVisual to render the blurred image
        var visual = new DrawingVisual();
        visual.Effect = new BlurEffect
        {
            Radius = radius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        // Render to bitmap - add padding for blur overflow
        var padding = (int)Math.Ceiling(radius);
        var width = source.PixelWidth + padding * 2;
        var height = source.PixelHeight + padding * 2;

        var renderTarget = new RenderTargetBitmap(
            width,
            height,
            96, 96,
            PixelFormats.Pbgra32);

        // Offset the visual to center it (accounting for blur padding)
        var offsetVisual = new DrawingVisual();
        using (var context = offsetVisual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(padding, padding));
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.Pop();
        }
        offsetVisual.Effect = new BlurEffect
        {
            Radius = radius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };

        renderTarget.Render(offsetVisual);
        return renderTarget;
    }
}
