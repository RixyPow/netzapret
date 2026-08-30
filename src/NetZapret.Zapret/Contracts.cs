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

    /// <summary>
    /// Единственная папка пресетов — наша.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пресеты Zapret не читаются вовсе, и это решение, а не упущение.
    /// Их там две папки и полторы сотни файлов, из которых почти все —
    /// переборные варианты одной стратегии; какие показывать, приходилось
    /// решать за человека, и всякий выбор оказывался спорным. Плюс они
    /// менялись под нами при каждом обновлении Zapret.
    /// </para>
    /// <para>
    /// Теперь набор один и виден целиком: что положено сюда, то и предлагается.
    /// Списки доменов по-прежнему берутся у Zapret — пресет ссылается на них
    /// относительно рабочего каталога, которым при запуске winws2 остаётся
    /// корень установки.
    /// </para>
    /// </remarks>
    public static string PresetDirectory { get; } = FindPresetDirectory();

    /// <summary>
    /// Ищет папку пресетов рядом с программой, а при отладке — в исходниках.
    /// </summary>
    /// <remarks>
    /// Подъём по каталогам нужен только разработчику: программа запускается
    /// из <c>bin/Release/net8.0-windows</c>, а пресеты лежат в корне проекта.
    /// В поставке первая же проверка попадает в цель.
    /// </remarks>
    private static string FindPresetDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "presets");

        if (Directory.Exists(beside))
            return beside;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "presets");

            if (Directory.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return candidate;

            directory = directory.Parent;
        }

        return beside;
    }

    /// <summary>
    /// Файлы пресетов.
    /// </summary>
    /// <remarks>
    /// Читается заново при каждом обращении: пресеты кладут на ходу,
    /// и держать список в памяти значило бы требовать перезапуска там,
    /// где достаточно вернуться в меню.
    /// </remarks>
    public static IReadOnlyList<string> PresetFiles =>
        Directory.Exists(PresetDirectory)
            ? Directory.EnumerateFiles(PresetDirectory, "*.txt").OrderBy(p => p).ToList()
            : [];

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
    /// <para>
    /// Точное совпадение имеет приоритет над подстрокой. Иначе запрос
    /// «Universal V6» уводит на «Universal V6 voicefix»: тот сортируется
    /// раньше, потому что пробел идёт до точки. Пользователь при этом
    /// получает не тот десинк, о котором просил, и замечает не сразу.
    /// </para>
    /// </remarks>
    /// <param name="directory">
    /// Где искать; по умолчанию наша папка пресетов. Задаётся только
    /// проверками — сопоставление имён стоит проверять, не раскладывая
    /// файлы в настоящей папке пользователя.
    /// </param>
    public static string? FindPreset(string name, string? directory = null)
    {
        var folder = directory ?? PresetDirectory;

        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.txt").OrderBy(p => p).ToList()
            : [];

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
    /// живёт своей жизнью: обновилась — и списки, на которые ссылаются наши
    /// пресеты, могли измениться или исчезнуть.
    /// </para>
    /// <para>
    /// Признак установки — папка <c>lists</c>, а не <c>presets</c>. Пресеты
    /// теперь наши, и по ним Zapret больше не опознать: у собранной копии
    /// его папки пресетов нет вовсе.
    /// </para>
    /// </remarks>
    public static ZapretPaths? Discover(string? hint = null)
    {
        foreach (var candidate in Candidates(hint))
        {
            if (Directory.Exists(Path.Combine(candidate, "lists")))
                return new ZapretPaths { Root = candidate };
        }

        return null;
    }

    /// <summary>
    /// Все места, где может лежать Zapret, в порядке предпочтения.
    /// </summary>
    /// <remarks>
    /// Перечислением, а не одним ответом: части установки может не быть
    /// во встроенной копии. Каталог сервисов, например, в неё до сих пор
    /// не клался, и поиск, остановившийся на первом подходящем корне,
    /// объявлял каталог отсутствующим при живом рядом.
    /// </remarks>
    public static IEnumerable<string> Candidates(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint))
            yield return hint!;

        foreach (var bundled in BundledRoots())
            yield return bundled;

        yield return @"C:\Zapret\Dev";
        yield return @"C:\Zapret";
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
