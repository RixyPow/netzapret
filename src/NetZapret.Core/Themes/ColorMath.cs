namespace NetZapret.Core.Themes;

/// <summary>
/// Пересчёт цвета между RGB, HSV и HSL — для окна выбора цвета.
/// </summary>
/// <remarks>
/// Окно выбора устроено как в Telegram (снимок владельца 24.09): квадрат
/// насыщенности и яркости — это HSV, поля H/S/L — HSL, ниже R/G/B и HEX.
/// Три модели одного цвета должны сходиться, иначе правка одного поля
/// сдвигала бы цвет в другом, и выбор «уплывал» бы от каждого касания.
/// </remarks>
public static class ColorMath
{
    /// <summary>Оттенок 0–360, насыщенность и яркость 0–1.</summary>
    public static (double H, double S, double V) ToHsv(ThemeColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        return (Hue(r, g, b, max, delta), max == 0 ? 0 : delta / max, max);
    }

    public static ThemeColor FromHsv(double h, double s, double v, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;

        var (r, g, b) = Sector(h, c, x);

        return new ThemeColor(alpha, Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>Оттенок 0–360, насыщенность и светлота 0–1.</summary>
    public static (double H, double S, double L) ToHsl(ThemeColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double l = (max + min) / 2;
        double s = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * l - 1));

        return (Hue(r, g, b, max, delta), Math.Clamp(s, 0, 1), l);
    }

    public static ThemeColor FromHsl(double h, double s, double l, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;

        var (r, g, b) = Sector(h, c, x);

        return new ThemeColor(alpha, Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>
    /// Чёрный или белый — что читается на этом цвете лучше.
    /// </summary>
    /// <remarks>
    /// Для подписи на главной кнопке: художник выбирает заливку, а подпись
    /// подбирается сама, иначе первая же яркая заливка с белой подписью
    /// не прошла бы проверку и тема не применилась бы.
    /// </remarks>
    public static ThemeColor ReadableOn(ThemeColor fill)
    {
        var black = new ThemeColor(255, 10, 10, 10);
        var white = new ThemeColor(255, 255, 255, 255);

        return ThemeContrast.Ratio(black.Luminance, fill.Luminance) >= ThemeContrast.Ratio(white.Luminance, fill.Luminance)
            ? black
            : white;
    }

    private static double Hue(double r, double g, double b, double max, double delta)
    {
        if (delta == 0)
            return 0;

        double h = max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);

        return h < 0 ? h + 360 : h;
    }

    private static (double R, double G, double B) Sector(double h, double c, double x) => h switch
    {
        < 60 => (c, x, 0),
        < 120 => (x, c, 0),
        < 180 => (0, c, x),
        < 240 => (0, x, c),
        < 300 => (x, 0, c),
        _ => (c, 0, x),
    };

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
