using System.Net;
using System.Text;

namespace NetZapret.Proxy;

/// <summary>Одна строка файла hosts, как её видит редактор.</summary>
public sealed record HostsEntry
{
    /// <summary>Номер строки в файле, считая с нуля.</summary>
    public required int Line { get; init; }

    public required string Address { get; init; }

    /// <summary>Имена в строке; их бывает несколько.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Действует ли запись, или закомментирована.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Комментарий в конце строки — обычно подпись, кто её поставил.</summary>
    public string? Note { get; init; }

    public string Describe() => $"{Address} → {string.Join(", ", Names)}";
}

/// <summary>Что известно про адрес, к которому прибиты имена.</summary>
public sealed record PinHealth
{
    public required string Address { get; init; }

    /// <summary>Сколько имён на него смотрит.</summary>
    public required int Names { get; init; }

    /// <summary>Отвечает ли он вообще.</summary>
    public required bool Alive { get; init; }

    /// <summary>
    /// Удалось ли вообще проверить.
    /// </summary>
    /// <remarks>
    /// Непроверенное — не молчащее. Адрес IPv6 в сети без IPv6 молчит
    /// не потому, что мёртв, и объявлять его мёртвым значит советовать
    /// выключить рабочую запись.
    /// </remarks>
    public bool Tested { get; init; } = true;

    /// <summary>Почему не проверяли.</summary>
    public string? Skipped { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Правит файл hosts: включает и выключает записи.
/// </summary>
/// <remarks>
/// <para>
/// Осторожность здесь не формальность, а суть. Файл системный, общий для всех
/// программ, и ведём его не только мы: записи ставит редактор из Zapret GUI,
/// ставят руками, ставят чужие наборы для обхода. Стереть чужую строку значит
/// сломать то, что человек настраивал не у нас и чинить будет не у нас.
/// </para>
/// <para>
/// Отсюда два правила. Выключение — это комментарий, а не удаление: запись
/// сохраняется и возвращается тем же движением. И перед всякой записью
/// делается копия рядом с файлом — не «на всякий случай», а потому что
/// единственная ошибка здесь стоит работающей сети.
/// </para>
/// </remarks>
public static class HostsEditor
{
    /// <summary>
    /// Метка, которой мы подписываем свои строки.
    /// </summary>
    /// <remarks>
    /// Нужна, чтобы отличать своё от чужого. Без неё «убрать всё, что мы
    /// добавили» невыполнимо: в файле не остаётся следа, кто автор строки,
    /// и любая чистка становится чисткой чужого заодно.
    /// </remarks>
    public const string Signature = "# netzapret";

    /// <summary>
    /// Разбирает файл построчно, сохраняя и выключенные записи.
    /// </summary>
    /// <remarks>
    /// В отличие от <see cref="HostsFile.Read"/>, который отвечает на вопрос
    /// «во что разрешится имя», здесь нужен сам файл: номера строк, порядок,
    /// закомментированные записи. Иначе редактировать нечего.
    /// </remarks>
    public static IReadOnlyList<HostsEntry> Parse(string? path = null)
    {
        var entries = new List<HostsEntry>();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path ?? HostsFile.DefaultPath);
        }
        catch (Exception)
        {
            return entries;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();

            if (line.Length == 0)
                continue;

            bool enabled = true;

            // Закомментированная строка может быть выключенной записью,
            // а может быть настоящим комментарием. Отличаем разбором:
            // если после решёток идёт адрес и имя — это запись.
            if (line.StartsWith('#'))
            {
                enabled = false;
                line = line.TrimStart('#').Trim();
            }

            string? note = null;
            int hash = line.IndexOf('#');

            if (hash >= 0)
            {
                note = line[(hash + 1)..].Trim();
                line = line[..hash].Trim();
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address))
                continue;

            entries.Add(new HostsEntry
            {
                Line = i,
                Address = address.ToString(),
                Names = parts.Skip(1).ToList(),
                Enabled = enabled,
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
            });
        }

        return entries;
    }

    /// <summary>
    /// Включает или выключает названные строки.
    /// </summary>
    /// <param name="lines">Номера строк из <see cref="HostsEntry.Line"/>.</param>
    /// <returns>Путь к копии, сделанной перед правкой.</returns>
    /// <remarks>
    /// Правит ровно указанные строки и не трогает ничего вокруг: остальной
    /// файл переписывается байт в байт, включая чужие комментарии и порядок.
    /// Переписывание «как мы понимаем формат» стёрло бы то, чего мы не поняли.
    /// </remarks>
    public static string SetEnabled(IReadOnlyCollection<int> lines, bool enabled, string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var content = File.ReadAllLines(target);
        var backup = Backup(target);

        foreach (var index in lines)
        {
            if (index < 0 || index >= content.Length)
                continue;

            var line = content[index];

            if (enabled)
            {
                var bare = line.TrimStart();

                if (bare.StartsWith('#'))
                    content[index] = bare.TrimStart('#').TrimStart();
            }
            else if (!line.TrimStart().StartsWith('#'))
            {
                content[index] = "# " + line;
            }
        }

        Write(target, content);
        return backup;
    }

    /// <summary>
    /// Добавляет свою запись — или правит её, если такая уже есть.
    /// </summary>
    /// <remarks>
    /// Только свои: чужую строку с тем же именем мы не переписываем, а выносим
    /// отказ наружу. Молча подменить чужой адрес значит сломать чужую настройку
    /// так, что виноватым окажется не тот.
    /// </remarks>
    public static string Add(string name, IPAddress address, string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var content = File.ReadAllLines(target).ToList();
        var backup = Backup(target);

        var ours = Parse(target)
            .FirstOrDefault(e => e.Note is not null
                && e.Note.Contains("netzapret", StringComparison.OrdinalIgnoreCase)
                && e.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

        var line = $"{address} {name}  {Signature}";

        if (ours is not null)
            content[ours.Line] = line;
        else
            content.Add(line);

        Write(target, content);
        return backup;
    }

    /// <summary>
    /// Проверяет, отвечают ли адреса, к которым прибиты имена.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ради этого редактор и заводится. Прибитый мёртвый адрес — чистый вред:
    /// он перебивает и наш маршрут, и обычный DNS, а сам не отвечает, и со
    /// стороны это неотличимо от блокировки.
    /// </para>
    /// <para>
    /// Наблюдалось вживую: 670 имён на один адрес, переставший отвечать,
    /// и «умерший» ChatGPT, который не чинился ни десинком, ни туннелем.
    /// </para>
    /// <para>
    /// Стучимся по 443 — это то, ради чего такие адреса и ставят. Живым
    /// считается ответивший хоть чем-то: отказ в соединении тоже ответ,
    /// он означает, что хост есть.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<PinHealth>> CheckAsync(
        IReadOnlyList<HostsEntry> entries,
        CancellationToken cancellationToken)
    {
        var byAddress = entries
            .Where(e => e.Enabled)
            .GroupBy(e => e.Address, StringComparer.Ordinal)
            .ToList();

        var result = new List<PinHealth>();

        // Спрашивается один раз: без IPv6 в сети всякий адрес IPv6 молчит,
        // и без этой поправки проверка объявила бы мёртвыми рабочие записи.
        bool haveIpV6 = await HasIpV6Async(cancellationToken);

        foreach (var group in byAddress)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Петля и нули ставят, чтобы имя не открывалось. Это не мёртвый
            // адрес, а намеренная заглушка, и тревожить о ней незачем.
            if (IPAddress.TryParse(group.Key, out var parsed) && BlockCheck.IsStub(parsed))
                continue;

            int names = group.Sum(g => g.Names.Count);

            if (!haveIpV6 && parsed?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                result.Add(new PinHealth
                {
                    Address = group.Key,
                    Names = names,
                    Alive = false,
                    Tested = false,
                    Skipped = "в этой сети нет IPv6",
                });

                continue;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool alive = await AnswersAsync(group.Key, cancellationToken);

            result.Add(new PinHealth
            {
                Address = group.Key,
                Names = names,
                Alive = alive,
                Elapsed = watch.Elapsed,
            });
        }

        return result.OrderByDescending(r => r.Names).ToList();
    }

    /// <summary>Есть ли в этой сети IPv6 вообще.</summary>
    /// <remarks>
    /// Проверяется соединением к публичному резолверу Google по IPv6.
    /// Наличие адреса на адаптере не годится: Windows раздаёт себе локальные
    /// адреса и без всякой связи наружу.
    /// </remarks>
    private static async Task<bool> HasIpV6Async(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            await client.ConnectAsync(IPAddress.Parse("2001:4860:4860::8888"), 53, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> AnswersAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            await client.ConnectAsync(IPAddress.Parse(address), 443, timeout.Token);
            return true;
        }
        catch (System.Net.Sockets.SocketException ex)
            when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            // Отказано — значит хост есть и отвечает, просто не слушает порт.
            // Для нашей цели это живой адрес.
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Копия файла перед правкой.
    /// </summary>
    /// <remarks>
    /// Рядом с оригиналом и с отметкой времени: восстановление должно быть
    /// очевидным действием без нашего участия — переименовать и всё.
    /// </remarks>
    private static string Backup(string path)
    {
        var backup = $"{path}.netzapret-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(path, backup, overwrite: true);
        return backup;
    }

    /// <summary>
    /// Записывает файл без BOM и с концами строк Windows.
    /// </summary>
    /// <remarks>
    /// BOM в hosts ломает разбор первой строки у части программ, а сам файл
    /// исторически ASCII с CRLF. Отступать от этого нам незачем.
    /// </remarks>
    private static void Write(string path, IReadOnlyList<string> lines) =>
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

    /// <summary>Сбрасывает кэш DNS; без этого правка не вступит в силу.</summary>
    /// <remarks>
    /// Windows держит разрешённые имена с большим сроком жизни, и записи
    /// из hosts в том числе. Без сброса человек правит файл, ничего
    /// не меняется, и он справедливо решает, что редактор не работает.
    /// </remarks>
    public static bool FlushDns()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
