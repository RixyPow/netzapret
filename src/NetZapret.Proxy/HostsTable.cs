using System.Net;

namespace NetZapret.Proxy;

/// <summary>Что за адрес стоит у прибитого имени — от этого зависит, тронет ли его десинк.</summary>
public enum HostsPinKind
{
    /// <summary>Чужой узел, через который ходит имя. Щит десинка пропускает его без обработки.</summary>
    Proxy,

    /// <summary>Адрес самого сервиса (<see cref="HostsEditor.DesyncMark"/>): щит его не берёт, рецепт остаётся.</summary>
    Service,

    /// <summary>Петля или нули: имя закрыто намеренно.</summary>
    Stub,
}

/// <summary>Одно имя одной строки hosts — строка таблицы раздела «Файл hosts».</summary>
public sealed record HostsRow
{
    /// <summary>Номер строки в файле, считая с нуля.</summary>
    public required int Line { get; init; }

    public required string Name { get; init; }

    public required string Address { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>Комментарий в конце строки.</summary>
    public string? Note { get; init; }

    /// <summary>Кто ведёт строку: имя блока или раздела; <c>null</c> — вне блоков.</summary>
    public string? Source { get; init; }

    /// <summary>Строка лежит прямо в нашем блоке, а не во вложенном чужом.</summary>
    public required bool Ours { get; init; }

    public required HostsPinKind Kind { get; init; }

    /// <summary>
    /// Имя уже прибито действующей строкой выше: Windows берёт первую, эта не действует.
    /// </summary>
    public bool Repeat { get; init; }
}

/// <summary>
/// Файл hosts таблицей: каждое имя строкой, с источником и видом пина.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 19.10 под редизайн раздела по макету владельца: одна таблица
/// с отбором вместо «наших» списком и «чужих» в свёрнутой карточке. Источник
/// и вид считаются здесь, а не в окне: их же захочет и <c>nz</c>.
/// </para>
/// <para>
/// Источник — самый внутренний блок. В hosts владельца 19.10 блок Zapret GUI
/// лежал внутри нашего (строки 136–190 внутри 131–275), и звать его записи
/// нашими значило бы приписать себе то, что ведёт другая программа.
/// </para>
/// </remarks>
public static class HostsTable
{
    /// <summary>Как называется наш блок в колонке «Источник».</summary>
    public const string OurSource = "NetZapret";

    public static IReadOnlyList<HostsRow> Read(string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;

        string[] lines;

        try
        {
            lines = File.ReadAllLines(target);
        }
        catch (Exception)
        {
            return [];
        }

        return Build(HostsEditor.Parse(target), lines);
    }

    /// <param name="entries">Разобранные записи — <see cref="HostsEditor.Parse"/>.</param>
    /// <param name="lines">Сам файл: по нему узнаётся, в каком блоке строка.</param>
    public static IReadOnlyList<HostsRow> Build(IReadOnlyList<HostsEntry> entries, IReadOnlyList<string> lines)
    {
        var sources = SourcesOf(lines);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<HostsRow>();

        foreach (var entry in entries.OrderBy(e => e.Line))
        {
            var source = sources.GetValueOrDefault(entry.Line);
            bool ours = source == OurSource;
            bool stub = IPAddress.TryParse(entry.Address, out var address) && BlockCheck.IsStub(address);

            foreach (var name in entry.Names)
            {
                // Выключенная строка повтором не бывает и сама не заслоняет
                // нижние: Windows её не читает.
                bool repeat = entry.Enabled && !seen.Add(name);

                rows.Add(new HostsRow
                {
                    Line = entry.Line,
                    Name = name,
                    Address = entry.Address,
                    Enabled = entry.Enabled,
                    Note = entry.Note,
                    Source = source,
                    Ours = ours,
                    Kind = stub
                        ? HostsPinKind.Stub
                        : ours && Marked(entry.Note)
                        ? HostsPinKind.Service
                        : HostsPinKind.Proxy,
                    Repeat = repeat,
                });
            }
        }

        return rows;
    }

    /// <summary>
    /// Строка → кто её ведёт.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Блоки — <c># &gt;&gt;&gt; … &gt;&gt;&gt;</c> до <c># &lt;&lt;&lt; … &lt;&lt;&lt;</c>,
    /// как у нас и у Zapret GUI, и могут быть вложены — тогда строка того,
    /// что внутри.
    /// </para>
    /// <para>
    /// Разделы — <c># --- имя ---</c> до пустой строки: так Zapret 2 GUI
    /// подписывает свои пины Telegram («Telegram Proxy (auto-managed by
    /// Zapret 2 GUI)»), без закрывающей строки.
    /// </para>
    /// </remarks>
    internal static Dictionary<int, string> SourcesOf(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<int, string>();
        var blocks = new Stack<string>();
        string? section = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim().TrimStart('﻿');

            if (line.StartsWith("# >>>", StringComparison.Ordinal))
            {
                blocks.Push(Display(line.Trim('#', ' ', '>')));
                section = null;
                continue;
            }

            if (line.StartsWith("# <<<", StringComparison.Ordinal))
            {
                if (blocks.Count > 0)
                    blocks.Pop();

                section = null;
                continue;
            }

            if (line.StartsWith("# ---", StringComparison.Ordinal) && line.EndsWith("---", StringComparison.Ordinal))
            {
                section = Display(line.Trim('#', ' ', '-'));
                continue;
            }

            if (line.Length == 0)
            {
                section = null;
                continue;
            }

            if ((section ?? (blocks.Count > 0 ? blocks.Peek() : null)) is { } source)
                result[i] = source;
        }

        return result;
    }

    /// <summary>Имя источника, понятное человеку: программы — их именем, прочее — как написано.</summary>
    internal static string Display(string raw)
    {
        if (("# >>> " + raw + " >>>").Equals(HostsEditor.BlockBegin, StringComparison.Ordinal))
            return OurSource;

        if (raw.Contains("zapretgui", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Zapret 2 GUI", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Zapret GUI", StringComparison.OrdinalIgnoreCase))
        {
            return "Zapret GUI";
        }

        return raw.Length > 40 ? raw[..40] + "…" : raw;
    }

    private static bool Marked(string? note) =>
        note is not null && note.Contains(HostsEditor.DesyncMark, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Разбирает вставленный список: строка — адрес и имена в любом порядке.
    /// </summary>
    /// <returns>Имя → адрес и строки, которые не разобрались (как были).</returns>
    /// <remarks>
    /// Порядок любой, потому что вставляют и из hosts («адрес имя»), и из
    /// заметок («имя адрес»). Решётка и всё за ней — комментарий. Ссылка
    /// вместо имени сводится к имени: люди вставляют адрес из браузера.
    /// Строка без адреса или без имени не угадывается, а возвращается —
    /// молча пропущенная выглядела бы прибитой.
    /// </remarks>
    public static (IReadOnlyDictionary<string, string> Pins, IReadOnlyList<string> Bad) ParseList(string text)
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int hash = line.IndexOf('#');

            if (hash >= 0)
                line = line[..hash].Trim();

            if (line.Length == 0)
                continue;

            var parts = line.Split((char[]?)[' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            var addresses = parts.Where(p => IPAddress.TryParse(p, out _)).ToList();
            var names = parts.Except(addresses).Select(Name).Where(n => n is not null).Cast<string>().ToList();

            if (addresses.Count != 1 || names.Count == 0 || names.Count != parts.Length - 1)
            {
                bad.Add(raw.Trim());
                continue;
            }

            var address = IPAddress.Parse(addresses[0]).ToString();

            foreach (var name in names)
                pins[name] = address;
        }

        return (pins, bad);
    }

    /// <summary>
    /// Имя для hosts из того, что ввели: без схемы, пути и звёздочки; <c>null</c> — не имя.
    /// </summary>
    public static string? Name(string input)
    {
        var name = input.Trim().Trim('/').ToLowerInvariant();

        if (name.Contains("://"))
            name = name.Split("://")[1];

        name = name.Split('/')[0].TrimStart('*', '.');

        return name.Length == 0 || !name.Contains('.') || name.Contains(' ') || IPAddress.TryParse(name, out _)
            ? null
            : name;
    }
}
