namespace RevitWallsPlugin.Services;

/// <summary>
/// Parses color strings from the BIMy payload into canonical "#rrggbb" form
/// or a Revit <see cref="Autodesk.Revit.DB.Color"/>. Accepts hex ("#rrggbb",
/// "rrggbb", shorthand "#rgb") and CSS ("rgb(r,g,b)", "rgba(r,g,b,a)" —
/// alpha is dropped; percent components supported). Returns null for
/// missing or unrecognised input so callers can treat the absence of a
/// color as "no color" rather than silently defaulting to gray.
/// </summary>
internal static class ImportColor
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open < 0 || close <= open) return null;
            var parts = s.Substring(open + 1, close - open - 1).Split(',');
            if (parts.Length < 3) return null;
            if (!TryParseByteComponent(parts[0], out var r)) return null;
            if (!TryParseByteComponent(parts[1], out var g)) return null;
            if (!TryParseByteComponent(parts[2], out var b)) return null;
            return $"#{r:x2}{g:x2}{b:x2}";
        }

        var body = s.StartsWith("#") ? s.Substring(1) : s;
        if (body.Length == 3)
        {
            var sb = new System.Text.StringBuilder(6);
            foreach (var c in body) { sb.Append(c); sb.Append(c); }
            body = sb.ToString();
        }
        if (body.Length != 6) return null;
        foreach (var c in body)
            if (!IsHexDigit(c)) return null;
        return "#" + body.ToLowerInvariant();
    }

    public static Autodesk.Revit.DB.Color ToRevit(string hex)
    {
        var s = hex.StartsWith("#") ? hex.Substring(1) : hex;
        if (s.Length != 6) return new Autodesk.Revit.DB.Color(160, 160, 160);
        try
        {
            var r = Convert.ToByte(s.Substring(0, 2), 16);
            var g = Convert.ToByte(s.Substring(2, 2), 16);
            var b = Convert.ToByte(s.Substring(4, 2), 16);
            return new Autodesk.Revit.DB.Color(r, g, b);
        }
        catch
        {
            return new Autodesk.Revit.DB.Color(160, 160, 160);
        }
    }

    public static string? FromColor(Autodesk.Revit.DB.Color c)
    {
        if (c is null || !c.IsValid) return null;
        return $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
    }

    private static bool TryParseByteComponent(string raw, out byte value)
    {
        value = 0;
        var s = raw.Trim();
        var isPercent = s.EndsWith("%");
        if (isPercent) s = s[..^1].TrimEnd();

        if (!double.TryParse(
                s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n))
            return false;

        var scaled = isPercent ? Math.Clamp(n, 0, 100) / 100.0 * 255.0 : n;
        value = (byte)Math.Clamp(Math.Round(scaled), 0, 255);
        return true;
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
