// The Windows types the shared engine files name, so those files compile here as they are. Only what they use.
#pragma warning disable CA1050
namespace System.Windows.Media
{
    /// <summary>A chart colour (Metrics, TimeSeries).</summary>
    public readonly record struct Color(byte A, byte R, byte G, byte B)
    {
        public static Color FromRgb(byte r, byte g, byte b) => new(255, r, g, b);
    }

    public static class Colors
    {
        public static Color Orange { get; } = Color.FromRgb(0xff, 0xa5, 0x00);
        public static Color Red { get; } = Color.FromRgb(0xff, 0x00, 0x00);
        public static Color Gray { get; } = Color.FromRgb(0x80, 0x80, 0x80);
    }
}

namespace System.Windows
{
    /// <summary>What Shared asks of the application.</summary>
    public class Application { public static object Current => global::Waypoint.App.Instance; }
}
