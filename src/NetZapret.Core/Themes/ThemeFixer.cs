namespace NetZapret.Core.Themes;

/// <summary>
/// «Поправить нечитаемое»: сдвигает светлоту цветов, не прошедших проверку.
/// </summary>
/// <remarks>
/// <para>
/// Владелец, 24.09: поменял в редакторе «Фон окна» на красный — и тема
/// «вообще сдохла». Пояснения, «работает» и «закрыто» на красном дали 2.62,
/// 2.04 и 1.55 при пороге 4.5, проверка тему не пропустила. Сказать «не
/// читается» мало: человек выбрал фон и хочет его, а не таблицу контрастов.
/// </para>
/// <para>
/// Поэтому правится не то, что выбрали, а то, что на нём стоит: оттенок
/// и насыщенность сохраняются — зелёный остаётся зелёным, — а светлота
/// уходит от фонов, пока пара не прочтётся. Сначала в сторону, противную
/// фонам (светлее на тёмном), и только если там не вышло — в другую.
/// </para>
/// </remarks>
public static class ThemeFixer
{
    /// <summary>Что на чём обязано читаться и с каким порогом — таблица из системы дизайна.</summary>
    private static readonly (string Fg, string[] Bgs, double Required)[] Rules =
    [
        (ThemeSlots.Text, [ThemeSlots.Backdrop, ThemeSlots.Surface, ThemeSlots.Raised], ThemeContrast.Text),
        (ThemeSlots.Muted, [ThemeSlots.Backdrop, ThemeSlots.Surface, ThemeSlots.Raised], ThemeContrast.Text),
        (ThemeSlots.Accent, [ThemeSlots.Backdrop, ThemeSlots.Surface, ThemeSlots.Raised], ThemeContrast.Text),
        (ThemeSlots.Danger, [ThemeSlots.Backdrop, ThemeSlots.Surface, ThemeSlots.Raised], ThemeContrast.Text),
        (ThemeSlots.Warn, [ThemeSlots.Backdrop, ThemeSlots.Surface, ThemeSlots.Raised], ThemeContrast.Text),
        (ThemeSlots.Faint, [ThemeSlots.Surface], ThemeContrast.Large),
        (ThemeSlots.OnAccent, [ThemeSlots.AccentFill], ThemeContrast.Text),
        (ThemeSlots.WarnFill, [ThemeSlots.Surface], ThemeContrast.Large),
    ];

    /// <summary>Цвета с поправленной светлотой; что читалось — не трогается.</summary>
    public static Dictionary<string, ThemeColor> Fix(IReadOnlyDictionary<string, ThemeColor> colors)
    {
        var fixedColors = new Dictionary<string, ThemeColor>(colors);
        var backdrop = colors[ThemeSlots.Backdrop];

        foreach (var (fg, bgs, required) in Rules)
        {
            var backgrounds = bgs.Select(b => fixedColors[b].Over(backdrop)).ToList();
            var color = fixedColors[fg];

            if (Worst(color, backgrounds) >= required)
                continue;

            double average = backgrounds.Average(b => b.Luminance);

            // Светлее на тёмном, темнее на светлом; не вышло — в другую сторону.
            var first = Shift(color, backgrounds, required, lighter: average < 0.18);
            var result = first ?? Shift(color, backgrounds, required, lighter: average >= 0.18);

            fixedColors[fg] = result ?? (average < 0.18
                ? new ThemeColor(255, 255, 255, 255)
                : new ThemeColor(255, 0, 0, 0));
        }

        return fixedColors;
    }

    /// <summary>Какие слоты не проходят — чтобы отметить их в редакторе.</summary>
    public static IReadOnlyDictionary<string, string> Failing(IReadOnlyDictionary<string, ThemeColor> colors)
    {
        var result = new Dictionary<string, string>();
        var backdrop = colors[ThemeSlots.Backdrop];

        foreach (var (fg, bgs, required) in Rules)
        {
            foreach (var bg in bgs)
            {
                double ratio = ThemeContrast.Ratio(colors[fg].Luminance, colors[bg].Over(backdrop).Luminance);

                if (ratio < required && !result.ContainsKey(fg))
                    result[fg] = $"не читается на «{bg}»: {ratio:0.00} из {required:0.#}";
            }
        }

        return result;
    }

    private static double Worst(ThemeColor color, List<ThemeColor> backgrounds) =>
        backgrounds.Min(b => ThemeContrast.Ratio(color.Luminance, b.Luminance));

    private static ThemeColor? Shift(ThemeColor color, List<ThemeColor> backgrounds, double required, bool lighter)
    {
        var (h, s, l) = ColorMath.ToHsl(color);

        for (int step = 1; step <= 50; step++)
        {
            double next = lighter ? l + step * 0.02 : l - step * 0.02;

            if (next is < 0 or > 1)
                break;

            var candidate = ColorMath.FromHsl(h, s, next);

            if (Worst(candidate, backgrounds) >= required)
                return candidate;
        }

        return null;
    }
}
