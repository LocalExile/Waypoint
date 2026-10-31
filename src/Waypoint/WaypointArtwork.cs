using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Waypoint;

/// <summary>Recolors the reviewed raster artwork without replacing its waypoint geometry.
/// The source's bright cyan engravings carry the accent; neutral masonry follows the logo surface.
/// A bounded cached bitmap avoids per-frame work, effects and animation.</summary>
public static class WaypointArtwork
{
    static readonly BitmapSource source = Load();
    static readonly byte[] pixels = Read();
    static readonly byte[] contrastMask = ContrastMask();
    /// <summary>The artwork as painted.</summary>
    public static BitmapSource Original => source;
    static (Color Surface, Color Detail, Color Accent, bool HighContrast, bool StrongContrast)? lastKey;
    static BitmapSource? last;
    static BitmapSource Load()
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/Waypoint;component/waypoint-192.png"));
        image.Freeze(); return image;
    }
    static byte[] Read()
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        converted.CopyPixels(bytes, source.PixelWidth * 4, 0); return bytes;
    }
    static byte[] ContrastMask()
    {
        // Expand only the engraved cores by two source pixels so small High Contrast glyphs stay continuous.
        // This does not spread the painted bloom or add an outline around the masonry.
        int width = source.PixelWidth, height = source.PixelHeight;
        var mask = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (Math.Min(pixels[i], pixels[i + 1]) < 183 || pixels[i + 3] < 128) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        if (dx * dx + dy * dy <= 5 && x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                            mask[(y + dy) * width + x + dx] = 255;
            }
        return mask;
    }
    public static BitmapSource Create(Color surface, Color detail, Color accent, bool highContrast, bool strongContrast)
    {
        // In High Contrast the masonry disappears and the same engraved silhouette becomes crisp system ink.
        var key = (surface, detail, accent, highContrast, strongContrast);
        if (lastKey == key && last is not null) return last;
        var output = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            // Separate cyan light from neutral stone. Retain the soft falloff painted around the etched lines;
            // the unlit outer bricks have no light channel and therefore never acquire an accent outline.
            double energy = Math.Max(Math.Clamp((Math.Min(g, b) - 125) / 110, 0, 1),
                Math.Clamp((b - r - 40) / 130, 0, 1) * Math.Clamp((g - 65) / 150, 0, 1));
            double core = Math.Clamp((Math.Min(g, b) - 170) / 65, 0, 1);
            Color color;
            byte alpha = pixels[i + 3];
            if (highContrast)
            {
                color = accent;
                alpha = contrastMask[i / 4];
            }
            else
            {
                double texture = Math.Clamp((.2126 * r + .7152 * g + .0722 * b) / 180, 0, 1) * .48;
                var stone = ThemePalette.Mix(surface, detail, texture);
                double light = strongContrast ? core : Math.Pow(energy, .85);
                color = ThemePalette.Mix(stone, accent, light);
            }
            output[i] = color.B; output[i + 1] = color.G; output[i + 2] = color.R; output[i + 3] = alpha;
        }
        var bitmap = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, output, source.PixelWidth * 4);
        bitmap.Freeze(); lastKey = key; return last = bitmap;
    }
}
