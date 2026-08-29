namespace NetZapret.Zapret;

/// <summary>
/// Управляет процессом winws2.
/// </summary>
/// <remarks>
/// Появился, когда задача сменилась с «ужиться с Zapret GUI» на «заменить его».
/// По форме это тот же супервизор, что и у sing-box: запустить с аргументами,
/// следить за живостью, остановить.
/// </remarks>
public interface IZapretOrchestrator : IAsyncDisposable
{
    ZapretState State { get; }

    /// <summary>Активный пресет либо <c>null</c>, если десинк выключен.</summary>
    ZapretPreset? ActivePreset { get; }

    Task StartAsync(ZapretPreset preset, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public enum ZapretState
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

/// <summary>
/// Расположение установки Zapret.
/// </summary>
public sealed record ZapretPaths
{
    /// <summary>Корень установки; относительно него заданы пути списков в пресетах.</summary>
    public required string Root { get; init; }

    public string PresetDirectory => Path.Combine(Root, "presets", "winws2");

    /// <summary>
    /// Встроенные пресеты Zapret, которые мы показываем наравне со своими.
    /// </summary>
    /// <remarks>
    /// Поимённо, а не всей папкой. В <c>presets/winws2_builtin</c> лежит
    /// больше сотни файлов — почти всё это переборные варианты одной и той же
    /// стратегии, и вывалить их в выбор значило бы утопить в них те два
    /// десятка, между которыми человек действительно выбирает. Здесь только
    /// названные: игровой набор, который иначе взять неоткуда.
    /// </remarks>
    public static IReadOnlyList<string> BuiltinPresetNames { get; } =
    [
        "Default v1 (game filter)",
        "Default v2 (game filter)",
        "Default v3 (game filter)",
        "Default v4 (game filter)",
        "Default v5 (game filter)",
    ];

    /// <summary>
    /// Все файлы пресетов: свои целиком, встроенные — только названные.
    /// </summary>
    public IReadOnlyList<string> PresetFiles
    {
        get
        {
            var files = new List<string>();

            if (Directory.Exists(PresetDirectory))
                files.AddRange(Directory.EnumerateFiles(PresetDirectory, "*.txt").OrderBy(p => p));

            var builtin = Path.Combine(Root, "presets", "winws2_builtin");

            foreach (var name in BuiltinPresetNames)
            {
                var path = Path.Combine(builtin, name + ".txt");

                if (File.Exists(path))
                    files.Add(path);
            }

            return files;
        }
    }

    public string ListDirectory => Path.Combine(Root, "lists");

    /// <summary>
    /// Путь к winws2.exe.
    /// </summary>
    /// <remarks>
    /// Движок лежит в подкаталоге <c>exe</c> рядом с WinDivert.dll и cygwin1.dll,
    /// а не в корне установки. Рабочим каталогом при запуске всё равно должен
    /// быть <see cref="Root"/>: пути к спискам в пресетах заданы относительно него.
    /// </remarks>
    public string ExecutablePath => Path.Combine(Root, "exe", "winws2.exe");

    /// <summary>
    /// Находит файл пресета по названию.
    /// </summary>
    /// <remarks>
    /// Точное совпадение имеет приоритет над подстрокой. Иначе запрос
    /// «Universal V6» уводит на «Universal V6 voicefix»: тот сортируется
    /// раньше, потому что пробел идёт до точки. Пользователь при этом
    /// получает не тот десинк, о котором просил, и замечает не сразу.
    /// </remarks>
    public string? FindPreset(string name)
    {
        var files = PresetFiles;

        if (files.Count == 0)
            return null;

        var wanted = name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;

        var exact = files.FirstOrDefault(p => string.Equals(
            Path.GetFileNameWithoutExtension(p), wanted, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
            return exact;

        return files
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileNameWithoutExtension(p).Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Ищет Zapret: сперва встроенную копию, затем установку в системе.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Встроенная копия впереди намеренно. Она кладётся рядом с программой
    /// при сборке и версионируется вместе с ней, тогда как установка в системе
    /// живёт своей жизнью: обновилась — и пресет, на который мы ссылаемся,
    /// может измениться или исчезнуть, а выяснится это при запуске.
    /// </para>
    /// <para>
    /// Установка при этом остаётся запасным вариантом: собранная из исходников
    /// копия без шага упаковки движков её не имеет, и без этого отката
    /// программа у разработчика просто не находила бы десинк.
    /// </para>
    /// </remarks>
    public static ZapretPaths? Discover(string? hint = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(hint))
            candidates.Add(hint!);

        candidates.AddRange(BundledRoots());

        candidates.Add(@"C:\Zapret\Dev");
        candidates.Add(@"C:\Zapret");

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "presets", "winws2")))
                return new ZapretPaths { Root = candidate };
        }

        return null;
    }

    /// <summary>Каталог встроенных движков — <c>engines/</c> рядом с программой.</summary>
    public const string BundleDirectory = "engines";

    /// <summary>
    /// Расположение встроенной копии — только рядом с программой.
    /// </summary>
    /// <remarks>
    /// Без подъёма по каталогам: сборка кладёт движки ровно сюда, а поиск
    /// вверх до корня диска подхватывал бы посторонние каталоги с таким же
    /// именем — и молча запускал бы чужой winws2 вместо своего.
    /// </remarks>
    private static IEnumerable<string> BundledRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, BundleDirectory, "zapret");
    }
}
