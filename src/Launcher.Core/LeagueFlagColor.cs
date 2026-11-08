using System.Globalization;

namespace Launcher.Core;

/// <summary>Personal flag-frame colour. Native artwork is never tinted or rewritten.</summary>
public static class LeagueFlagColor
{
    public static string Normalize(string? value) => value is "accent" or "red" or "neutral" ? value
        : TryHex(value, out var rgb) ? $"#{rgb:X6}" : "auto";
    public static bool TryHex(string? value, out uint rgb)
    { rgb = 0; return value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit)
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb); }
    public static string Role(string? value, bool hardcore) => Normalize(value) switch
    { "accent" => "Shell.Accent", "red" => "Shell.Error", "neutral" => "Shell.TextSecondary", _ => hardcore ? "Shell.Error" : "Shell.Accent" };
    public static double Contrast(uint a, uint b)
    {
        static double Light(uint rgb)
        {
            static double Channel(uint c) { double v = c / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Channel(rgb >> 16 & 255) + .7152 * Channel(rgb >> 8 & 255) + .0722 * Channel(rgb & 255);
        }
        double x = Light(a), y = Light(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    public static uint Readable(uint color, IReadOnlyCollection<uint> surfaces, bool grayscale = false)
    {
        if (grayscale) { uint g = (uint)Math.Round(.2126 * (color >> 16 & 255) + .7152 * (color >> 8 & 255) + .0722 * (color & 255)); color = g * 0x010101; }
        if (surfaces.All(s => Contrast(color, s) >= 3)) return color;
        uint best = color; double score = surfaces.Min(s => Contrast(color, s));
        for (int step = 1; step <= 100; step++)
        {
            foreach (uint end in new uint[] { 0, 0xffffff })
            {
                uint Mix(int shift) => (uint)Math.Round((color >> shift & 255) * (1 - step / 100d) + (end >> shift & 255) * step / 100d);
                uint candidate = Mix(16) << 16 | Mix(8) << 8 | Mix(0);
                double candidateScore = surfaces.Min(s => Contrast(candidate, s));
                if (candidateScore >= 3) return candidate;
                if (candidateScore > score) { best = candidate; score = candidateScore; }
            }
        }
        return best;
    }
}
