using System.Text.RegularExpressions;

namespace NetZapret.Zapret;

/// <summary>Один модуль Lua и приёмы, которые он даёт.</summary>
public sealed record LuaModule
{
    /// <summary>Имя файла — то же, что стоит после <c>@lua/</c> в пресете.</summary>
    public required string File { get; init; }

    /// <summary>Имена приёмов, годных для <c>--lua-desync</c>.</summary>
    public required IReadOnlyList<string> Functions { get; init; }
}

/// <summary>
/// Что движок умеет и что из этого подключил пресет.
/// </summary>
/// <remarks>
/// <para>
/// Приёмы десинка живут в модулях Lua рядом с winws2, а пресет подключает
/// их перечнем <c>--lua-init</c>. Подключает не все: Universal V8 берёт
/// восемь модулей из пятнадцати, и приёма из невзятого для него
/// не существует.
/// </para>
/// <para>
/// Знать это нужно до запуска. winws2 на неизвестный приём отвечает
/// «desync function 'flood_white' does not exist» и не поднимается вовсе —
/// то есть опечатка или рецепт не из того модуля оставляют человека
/// без десинка целиком, а не без одного правила.
/// </para>
/// <para>
/// Список берётся из самих файлов, а не пишется рядом руками. Модули
/// приходят вместе с движком и обновляются с ним; переписанный от руки
/// перечень разошёлся бы с ними на первом же обновлении, и разошёлся бы
/// молча.
/// </para>
/// </remarks>
public static class LuaModules
{
    /// <summary>
    /// Объявление приёма.
    /// </summary>
    /// <remarks>
    /// Только глобальные <c>function имя(ctx, desync)</c>: такова подпись,
    /// по которой движок их вызывает. <c>local function</c> — внутренняя
    /// кухня модуля, из пресета она недоступна, и показывать её в каталоге
    /// значило бы обещать несуществующее.
    /// </remarks>
    private static readonly Regex Declaration = new(
        @"^function\s+([A-Za-z_]\w*)\s*\(\s*ctx\s*,\s*desync\s*\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Init = new(
        @"^--lua-init=@(?:.*/)?(?<file>[\w.\-]+\.lua)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Blob = new(
        @"^--blob=(?<name>[\w.\-]+):",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Разобрать папку модулей; отсутствующая папка — пустой список.</summary>
    /// <remarks>
    /// Пустой список, а не исключение: каталог рецептов показывается
    /// и до того, как движок распакован, — и «приёмов не видно» там уместнее,
    /// чем сорванная вкладка.
    /// </remarks>
    public static IReadOnlyList<LuaModule> Scan(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "*.lua")
            .Select(path => new LuaModule
            {
                File = Path.GetFileName(path),
                Functions = Declaration.Matches(ReadQuietly(path))
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            })
            .Where(module => module.Functions.Count > 0)
            .OrderBy(module => module.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Приём → модуль, который его даёт.</summary>
    /// <remarks>
    /// Один приём в двух модулях встречается: <c>wgobfs</c> объявлен
    /// и в zapret-obfs.lua, и в zapret-wgobfs.lua. Побеждает первый по
    /// алфавиту — выбор произвольный, но устойчивый, а нам от него нужно
    /// лишь имя файла для подсказки «подключите такой-то модуль».
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Providers(
        IEnumerable<LuaModule> modules)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            foreach (var name in module.Functions)
                map.TryAdd(name, module.File);
        }

        return map;
    }

    /// <summary>Модули, подключённые пресетом.</summary>
    public static IReadOnlySet<string> DeclaredBy(ZapretPreset preset) =>
        Init.Matches(string.Join('\n', preset.GlobalArguments))
            .Select(m => m.Groups["file"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Блобы, объявленные пресетом.
    /// </summary>
    /// <remarks>
    /// Нужны наравне с приёмами. Рецепт ссылается на блоб по имени,
    /// и неизвестное имя роняет запуск так же, как неизвестный приём.
    /// Проверено на себе: в примерах zapret-16kb.lua стоит <c>blob=bin_max</c>,
    /// а в наших пресетах тот же файл объявлен как <c>tls_max</c>.
    /// </remarks>
    public static IReadOnlySet<string> BlobsOf(ZapretPreset preset) =>
        Blob.Matches(string.Join('\n', preset.GlobalArguments))
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string ReadQuietly(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            // Модуль занят или недочитан — пусть выпадет из каталога.
            // Сорванная вкладка здесь хуже неполного списка.
            return string.Empty;
        }
    }
}
