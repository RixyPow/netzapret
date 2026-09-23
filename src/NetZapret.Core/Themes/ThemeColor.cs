using System.Globalization;

namespace NetZapret.Core.Themes;

/// <summary>Цвет темы: #RRGGBB или #AARRGGBB.</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        var value = text?.Trim();

        if (value is null || !value.StartsWith('#') || value.Length is not (7 or 9))
            return false;

        if (!uint.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return false;

        if (value.Length == 7)
            raw |= 0xFF000000;

        color = new ThemeColor((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
        return true;
    }

    public static ThemeColor Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException($"Не цвет: «{text}».");

    public bool Opaque => A == 255;

    /// <summary>Относительная яркость по WCAG 2 — непрозрачной составляющей.</summary>
    public double Luminance =>
        0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

    /// <summary>Этот цвет поверх другого — то, что видит глаз у полупрозрачного.</summary>
    public ThemeColor Over(ThemeColor below)
    {
        double a = A / 255.0;

        byte Mix(byte top, byte bottom) => (byte)Math.Round(top * a + bottom * (1 - a));

        return new ThemeColor(255, Mix(R, below.R), Mix(G, below.G), Mix(B, below.B));
    }

    public override string ToString() =>
        Opaque ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
