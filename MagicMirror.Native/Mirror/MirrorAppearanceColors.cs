using System.Globalization;
using Microsoft.Maui.Graphics;

namespace MagicMirror.Native.Mirror;

public static class MirrorAppearanceColors
{
    public const string DefaultBackgroundHex = "#F3F6EF";
    public const string DefaultTextHex = "#0D0F14";

    public static string NormalizeHex(string? value, string fallback)
    {
        if (TryNormalizeHex(value, out var normalized))
            return normalized;

        return TryNormalizeHex(fallback, out var normalizedFallback)
            ? normalizedFallback
            : DefaultTextHex;
    }

    public static Color ToColor(string? value, string fallback)
    {
        var hex = NormalizeHex(value, fallback);
        int r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromRgb(r, g, b);
    }

    private static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = "";
        var raw = (value ?? "").Trim();
        if (raw.Length == 0)
            return false;

        if (raw[0] == '#')
            raw = raw.Substring(1);

        if (raw.Length == 3)
            raw = string.Concat(raw.Select(ch => new string(ch, 2)));

        if (raw.Length != 6 || raw.Any(ch => !Uri.IsHexDigit(ch)))
            return false;

        normalized = "#" + raw.ToUpperInvariant();
        return true;
    }
}
