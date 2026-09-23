namespace NetZapret.Core.Themes;

/// <summary>Имена 14 цветовых слотов и ключи, под которыми их ждёт окно.</summary>
public static class ThemeSlots
{
    public const string Backdrop = "backdrop";
    public const string Surface = "surface";
    public const string Raised = "raised";
    public const string Border = "border";
    public const string Text = "text";
    public const string Muted = "muted";
    public const string Faint = "faint";
    public const string Accent = "accent";
    public const string AccentDim = "accent-dim";
    public const string Danger = "danger";
    public const string Warn = "warn";
    public const string AccentFill = "accent-fill";
    public const string OnAccent = "on-accent";
    public const string WarnFill = "warn-fill";

    public static IReadOnlyList<string> All { get; } =
    [
        Backdrop, Surface, Raised, Border, Text, Muted, Faint,
        Accent, AccentDim, Danger, Warn, AccentFill, OnAccent, WarnFill,
    ];

    /// <summary>
    /// Ключ ресурса в окне: <c>accent-fill</c> ⇄ <c>AccentFill</c>.
    /// </summary>
    /// <remarks>
    /// Та же связь, что записана в системе дизайна: имя токена и ключ
    /// в XAML связаны механически, и сверять их глазами не приходится.
    /// </remarks>
    public static string ResourceKey(string slot) =>
        string.Concat(slot.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

/// <summary>Пара цветов, не прошедшая порог.</summary>
public sealed record ContrastFailure(string Foreground, string Background, double Ratio, double Required)
{
    public override string ToString() =>
        $"{Foreground} на {Background}: {Ratio:0.00}:1, нужно {Required:0.#}:1";
}

/// <summary>
/// Проверка читаемости темы — по таблице пар из системы дизайна.
/// </summary>
/// <remarks>
/// <para>
/// Пороги WCAG 2: 4.5:1 для текста, 3:1 для крупного и для того, что
/// не читают, а замечают (точки состояния, третий план). Тема, не прошедшая
/// хоть одну пару, не применяется: показать нечитаемое окно хуже, чем
/// отказаться и назвать пару.
/// </para>
/// <para>
/// Свои темы проверены той же проверкой. Замер 23.09: у светлой провалились
/// три пары — подпись на главной кнопке 3.22, «внимание» на кнопке 4.37,
/// точки «внимание» на карточке 2.38, — и они исправлены до того, как
/// проверку потребовали от художников.
/// </para>
/// <para>
/// С картинкой фона <c>backdrop</c> — это не один цвет, а всё, что под
/// заголовками: картинка под затемнением. Поэтому текст на подложке
/// сверяется с самым тёмным и самым светлым участком затемнённой картинки;
/// их яркость меряет окно, у библиотеки картинок нет.
/// </para>
/// </remarks>
public static class ThemeContrast
{
    public const double Text = 4.5;
    public const double Large = 3.0;

    /// <summary>Сверяет все пары; пустой список — тема читается.</summary>
    /// <param name="backdropRange">
    /// Яркость самого тёмного и самого светлого участка фона под затемнением,
    /// по WCAG; <c>null</c> — фона нет, подложка сплошная.
    /// </param>
    public static IReadOnlyList<ContrastFailure> Check(Theme theme, (double Darkest, double Lightest)? backdropRange = null)
    {
        var failures = new List<ContrastFailure>();
        var backdrop = theme[ThemeSlots.Backdrop];

        // Полупрозрачная карточка — стекло над фоном: её цвет тот, что
        // получается поверх подложки.
        ThemeColor Solid(string slot) => theme[slot].Over(backdrop);

        void Pair(string fg, string bg, double required)
        {
            double ratio = Ratio(Solid(fg).Luminance, Solid(bg).Luminance);

            if (ratio < required)
                failures.Add(new ContrastFailure(fg, bg, ratio, required));
        }

        foreach (var fg in new[] { ThemeSlots.Text, ThemeSlots.Muted, ThemeSlots.Accent, ThemeSlots.Danger, ThemeSlots.Warn })
        {
            foreach (var bg in new[] { ThemeSlots.Backdrop, ThemeSlots.Surface, ThemeSlots.Raised })
                Pair(fg, bg, Text);
        }

        Pair(ThemeSlots.Faint, ThemeSlots.Surface, Large);
        Pair(ThemeSlots.OnAccent, ThemeSlots.AccentFill, Text);
        Pair(ThemeSlots.WarnFill, ThemeSlots.Surface, Large);

        // Заголовки вкладок и пункты меню стоят прямо на фоне. Меню и
        // заголовки — text и muted; проверяются с обоими краями картинки.
        if (backdropRange is { } range)
        {
            foreach (var fg in new[] { ThemeSlots.Text, ThemeSlots.Muted })
            {
                double l = Solid(fg).Luminance;
                double worst = Math.Min(Ratio(l, range.Darkest), Ratio(l, range.Lightest));

                if (worst < Text)
                    failures.Add(new ContrastFailure(fg, "фон-картинка", worst, Text));
            }
        }

        return failures;
    }

    public static double Ratio(double a, double b) =>
        (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}
