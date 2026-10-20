using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Шрифты, которые едут внутри программы, а не берутся из Windows.
/// </summary>
/// <remarks>
/// <para>
/// Список шрифтов в «Оформлении» — установленные в системе. Manrope
/// (владелец 20.10: «добавь мне Manrope в список шрифтов») в Windows нет,
/// и просить людей ставить шрифт ради одной программы — лишний шаг.
/// Поэтому файлы лежат ресурсом в сборке, а имя в настройках остаётся
/// просто «Manrope»: выбрали — нашёлся встроенный.
/// </para>
/// <para>
/// Шесть статических начертаний, Light–ExtraBold, из
/// github.com/googlefonts/manrope. Вариативный файл из Google Fonts не годится:
/// WPF оси веса не понимает и берёт начальный экземпляр, а у Manrope это
/// ExtraLight — всё окно было бы тонким, и полужирного не было бы вовсе.
/// Кириллица во всех шести — сверено картой символов 20.10.
/// </para>
/// <para>
/// Segoe UI стоит запасом в самом семействе: знак, которого в Manrope нет
/// (флаги-эмодзи в именах серверов, стрелки), берётся оттуда, а не
/// рисуется квадратом.
/// </para>
/// </remarks>
public static class BundledFonts
{
    // Полным адресом ресурса — как словари тем: относительный находится
    // только из самой программы, а из тестов нет.
    private const string Base = "pack://application:,,,/NetZapret;component/Assets/fonts/";

    private static readonly (string Name, string Folder)[] Known =
    [
        ("Manrope", "manrope"),
    ];

    /// <summary>Имена встроенных шрифтов — для списка в «Оформлении».</summary>
    public static IReadOnlyList<string> Names { get; } = Known.Select(k => k.Name).ToArray();

    /// <summary>Встроенное семейство по имени или <c>null</c>, если такого не возим.</summary>
    public static FontFamily? Find(string? name) =>
        Location(name) is { } location ? new FontFamily(new Uri(Base), location + ", Segoe UI") : null;

    /// <summary>
    /// Семейство по списку шрифтов темы («Manrope, Segoe UI»), если в нём есть
    /// встроенный; <c>null</c> — список весь системный.
    /// </summary>
    /// <remarks>
    /// Тема пишет шрифт именем, как системный, и до 20.10 «Manrope» в теме
    /// молча давал Segoe UI: в Windows его нет. Адрес ресурса — только базой
    /// семейства, а не в строке имён: в «pack://application:,,,/…» запятые,
    /// а строку имён WPF режет по запятым (20.10 так тест и упал — Segoe UI).
    /// Системные имена рядом с базой ищутся как обычно.
    /// </remarks>
    public static FontFamily? Family(string names)
    {
        var parts = names.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

        if (!parts.Any(n => Location(n) is not null))
            return null;

        return new FontFamily(new Uri(Base), string.Join(", ", parts.Select(n => Location(n) ?? n)));
    }

    /// <summary>Ссылка от <see cref="Base"/>: «./manrope/#Manrope»; <c>null</c> — шрифт не наш.</summary>
    private static string? Location(string? name)
    {
        foreach (var (known, folder) in Known)
        {
            if (string.Equals(known, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                return $"./{folder}/#{known}";
        }

        return null;
    }
}
