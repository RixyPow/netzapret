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
    public static FontFamily? Find(string? name)
    {
        foreach (var (known, folder) in Known)
        {
            if (string.Equals(known, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                return new FontFamily(new Uri(Base + folder + "/"), $"./#{known}, Segoe UI");
        }

        return null;
    }
}
