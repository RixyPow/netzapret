using NetZapret.Core;

namespace NetZapret.Zapret;

/// <summary>Чем подделка служит: приветствием TLS, первым пакетом QUIC или прочим UDP.</summary>
public enum FakeKind
{
    Tls,
    Quic,
    Udp,
    Other,
}

/// <summary>Образец подделки: встроенный в winws2 или файл из <c>bin\</c>.</summary>
/// <param name="Id">Как он хранится в настройках: имя файла (<c>stun2.bin</c>) или встроенное имя.</param>
/// <param name="Note">Что сказать сверх имени: содержимое не то, что обещает имя, или это копия другого образца.</param>
public sealed record FakeBlob(string Id, FakeKind Kind, bool BuiltIn, string? Note = null)
{
    /// <summary>Как назвать человеку: без <c>.bin</c> и без приставок.</summary>
    public string Title => BuiltIn ? Id : Path.GetFileNameWithoutExtension(Id);
}

/// <summary>Место в секции пресета, где стоит подделка.</summary>
/// <param name="Slot">Номер шага с <c>blob=</c> внутри секции, с нуля.</param>
/// <param name="Step">Шаг <c>--lua-desync</c> целиком — показать, что именно меняется.</param>
/// <param name="Blob">Образец по пресету: имя из <c>blob=</c>.</param>
/// <param name="BlobFile">Файл, которым пресет объявил этот образец; <c>null</c> — встроенный.</param>
/// <param name="Hints">Списки и фильтры секции — для поиска: у «Голосовых звонков/чатов» слова
/// «discord» нет в имени, а есть в <c>--filter-l7=stun,discord</c>.</param>
public sealed record FakeSlot(string Section, int Slot, string Step, string Blob, string? BlobFile, bool Udp, string Hints = "");

/// <summary>
/// Выбор подделки у секции пресета — без правки файла пресета.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №51 (SuzuyaJ74, 02.11): «надо бы в интерфейс добавить выбор
/// фейков… средний обыватель вряд ли полезет в файлы Universal V12 Lite». В №30
/// (Discord, пинг до 5000 мс) он же поменял подделку «Голосовых звонков/чатов»
/// с <c>quic_google</c> на <c>active_discord</c> и <c>stun2</c> — «пинга 5к либо
/// нет вообще, либо раз в пару часов на несколько секунд». У нас не замерено.
/// Все три его файла лежат в копии Zapret GUI (<c>bin\</c>), но V12 Lite их не
/// объявляет: из 66 объявленных образцов его секции берут три.
/// </para>
/// <para>
/// Выбор хранится в настройках по паре «пресет — имя секции» и подставляется
/// в готовую строку запуска (<see cref="Apply"/>): обновление пресета выбор
/// не стирает, а переименованная секция просто остаётся как в пресете.
/// Файл образца объявляется своим именем с приставкой <c>nz_fake_</c> — как
/// у game filter, чтобы не спорить с объявлениями пресета.
/// </para>
/// </remarks>
public static class FakeChoice
{
    /// <summary>Встроенные образцы winws2 (bol-van, <c>zapret-lib.lua</c>).</summary>
    public static IReadOnlyList<FakeBlob> BuiltIns { get; } =
    [
        new("fake_default_tls", FakeKind.Tls, true),
        new("fake_default_quic", FakeKind.Quic, true),
        new("fake_default_udp", FakeKind.Udp, true),
        new("fake_default_http", FakeKind.Other, true),
    ];

    private const string Prefix = "nz_fake_";

    /// <summary>Чем служит образец — по имени файла или встроенному имени.</summary>
    public static FakeKind KindOf(string id)
    {
        var name = Path.GetFileNameWithoutExtension(id).ToLowerInvariant();

        if (name.StartsWith("nz-", StringComparison.Ordinal))
            name = name[3..];

        if (name.StartsWith("tls", StringComparison.Ordinal) || name.Contains("clienthello", StringComparison.Ordinal)
            || name == "fake_default_tls")
            return FakeKind.Tls;

        if (name.StartsWith("quic", StringComparison.Ordinal) || name == "fake_default_quic")
            return FakeKind.Quic;

        if (name.StartsWith("stun", StringComparison.Ordinal) || name.StartsWith("active_", StringComparison.Ordinal)
            || name.StartsWith("dtls", StringComparison.Ordinal) || name.Contains("discord", StringComparison.Ordinal)
            || name.Contains("wireguard", StringComparison.Ordinal) || name.Contains("udp", StringComparison.Ordinal))
            return FakeKind.Udp;

        return FakeKind.Other;
    }

    /// <summary>Образцы, что есть: встроенные и файлы <c>bin\*.bin</c> корня Zapret.</summary>
    /// <remarks>
    /// <para>
    /// Вид — по содержимому, если его видно (<see cref="ContentKind"/>), иначе по имени.
    /// Имя врёт: в копии Zapret GUI <c>ACTIVE_DISCORD_UDP.bin</c> побайтно равен
    /// <c>quic_initial_steamcommunity_com.bin</c> — первый пакет QUIC с именем
    /// steamcommunity.com, а не пакет Discord (сверка 08.11, на это указал PR
    /// Flowseal №18613). По имени он числился «пакетом UDP», и SuzuyaJ74 в №51
    /// пробовал его на голосе Discord, не зная, что это QUIC от Steam.
    /// </para>
    /// <para>
    /// Побайтные копии называются: копия получает пометку «копия &lt;образец&gt;»,
    /// где образец — тот из одинаковых файлов, чьё имя совпадает с содержимым.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FakeBlob> Available(string? zapretRoot)
    {
        var bin = zapretRoot is null ? null : Path.Combine(zapretRoot, "bin");

        if (bin is null || !Directory.Exists(bin))
            return BuiltIns;

        var read = new List<(string Name, FakeKind ByName, FakeKind? ByContent, string? Hash)>();

        foreach (var name in Directory.EnumerateFiles(bin, "*.bin").Select(Path.GetFileName).OfType<string>()
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? bytes = null;

            try
            {
                var path = Path.Combine(bin, name);

                // Образцы — сотни байт, крупнее пакета не бывает; большое не читаем.
                if (new FileInfo(path).Length <= 64 * 1024)
                    bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            read.Add((name, KindOf(name), bytes is null ? null : ContentKind(bytes),
                bytes is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
        }

        var files = new List<FakeBlob>();

        foreach (var (name, byName, byContent, hash) in read)
        {
            var notes = new List<string>();
            var kind = byContent ?? byName;

            // Помечается только имя, которое обещает другое: «1.bin» или «fake_tls_3.bin»
            // ничего не обещают, им вид молча берётся из содержимого.
            if (byContent is { } content && byName != FakeKind.Other && content != byName)
            {
                notes.Add($"внутри — {KindWord(content)}");

                // Чья это копия — того из одинаковых, чьё имя честно и подробнее прочих.
                var original = hash is null
                    ? null
                    : read.Where(r => r.Hash == hash && r.Name != name && r.ByName == content)
                        .OrderByDescending(r => r.Name.Length)
                        .Select(r => r.Name)
                        .FirstOrDefault();

                notes.Add(original is null ? "хотя имя говорит иное" : $"копия {original}");
            }

            files.Add(new FakeBlob(name, kind, false, notes.Count == 0 ? null : string.Join(", ", notes)));
        }

        return [.. BuiltIns, .. files];
    }

    /// <summary>Вид по первым байтам; <c>null</c> — не узнан.</summary>
    /// <remarks>
    /// Запись рукопожатия TLS (<c>16 03 xx</c>), длинный заголовок QUIC версии 1
    /// или 2 (старшие два бита первого байта и версия следом), STUN — «магическое
    /// число» <c>2112A442</c> в байтах 4–7 (RFC 5389).
    /// </remarks>
    public static FakeKind? ContentKind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0x16 && bytes[1] == 0x03)
            return FakeKind.Tls;

        if (bytes.Length >= 5 && (bytes[0] & 0xC0) == 0xC0)
        {
            uint version = (uint)(bytes[1] << 24 | bytes[2] << 16 | bytes[3] << 8 | bytes[4]);

            if (version is 0x00000001 or 0x6B3343CF)
                return FakeKind.Quic;
        }

        if (bytes.Length >= 20 && bytes[4] == 0x21 && bytes[5] == 0x12 && bytes[6] == 0xA4 && bytes[7] == 0x42)
            return FakeKind.Udp;

        return null;
    }

    /// <summary>Вид словами — для пометки и для окна выбора.</summary>
    public static string KindWord(FakeKind kind) => kind switch
    {
        FakeKind.Tls => "приветствие TLS",
        FakeKind.Quic => "первый пакет QUIC",
        FakeKind.Udp => "пакет UDP",
        _ => "прочее",
    };

    /// <summary>Что из образцов годится в это место: по протоколу секции.</summary>
    public static IReadOnlyList<FakeBlob> Fitting(FakeSlot slot, IReadOnlyList<FakeBlob> available) =>
        available
            .Where(b => slot.Udp ? b.Kind is FakeKind.Quic or FakeKind.Udp : b.Kind is FakeKind.Tls or FakeKind.Other)
            .ToList();

    /// <summary>Места подделок в пресете: по секциям с именем, по порядку шагов.</summary>
    public static IReadOnlyList<FakeSlot> Slots(ZapretPreset preset)
    {
        var declared = Declarations(preset.GlobalArguments);
        var result = new List<FakeSlot>();

        foreach (var section in preset.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Name))
                continue;

            bool udp = IsUdp(section.RawArguments);
            var hints = string.Join(' ', section.RawArguments
                .Where(a => a.StartsWith("--hostlist", StringComparison.Ordinal) || a.StartsWith("--ipset", StringComparison.Ordinal)
                            || a.StartsWith("--filter-l7=", StringComparison.Ordinal))
                .Select(a => a.Split('=', 2).Last()));
            int slot = 0;

            foreach (var step in section.DesyncRecipes)
            {
                if (BlobOf(step) is not { } blob)
                    continue;

                result.Add(new FakeSlot(section.Name, slot++, step, blob, declared.GetValueOrDefault(blob), udp, hints));
            }
        }

        return result;
    }

    /// <summary>
    /// Ключ выбора у секций game filter — вместо имени пресета.
    /// </summary>
    /// <remarks>
    /// Обсуждение №64 (Rocket League, 11.11): с game filter игра висит на чёрном
    /// экране — все её серверы Amazon лежат в ipset-all, и подделка встаёт перед
    /// каждым UDP-потоком к ним. Секции game filter одни на любой пресет, поэтому
    /// и выбор у них один на все пресеты — и на все игры сразу: образец,
    /// подобранный под одну игру, получат и остальные.
    /// </remarks>
    public const string GameFilterKey = "game filter";

    /// <summary>Места подделок в секциях game filter — те же, что он дописывает в строку.</summary>
    public static IReadOnlyList<FakeSlot> GameFilterSlots()
    {
        var arguments = new List<string>();
        GameFilter.Apply(arguments);

        var declared = Declarations(arguments);
        var result = new List<FakeSlot>();
        int start = arguments.IndexOf("--new");

        while (start >= 0 && start < arguments.Count)
        {
            int end = arguments.IndexOf("--new", start + 1);

            if (end < 0)
                end = arguments.Count;

            var section = arguments.Skip(start + 1).Take(end - start - 1).ToList();
            var name = section.FirstOrDefault(a => a.StartsWith("--name=", StringComparison.Ordinal))?["--name=".Length..];
            int slot = 0;

            foreach (var step in section.Where(a => a.StartsWith("--lua-desync=", StringComparison.Ordinal)).Select(a => a["--lua-desync=".Length..]))
            {
                if (name is null || BlobOf(step) is not { } blob)
                    continue;

                result.Add(new FakeSlot(name, slot++, step, blob, declared.GetValueOrDefault(blob), IsUdp(section), "game filter ipset-all игры"));
            }

            start = end;
        }

        return result;
    }

    /// <summary>
    /// Подставляет выбранные подделки в готовую строку запуска; возвращает,
    /// сколько мест заменено.
    /// </summary>
    /// <remarks>
    /// Берёт выбор у пресета <paramref name="presetName"/> и у секций game filter
    /// (<see cref="GameFilterKey"/>): их имена своих секций у пресета не бывает.
    /// </remarks>
    public static int Apply(List<string> arguments, string? presetName, IReadOnlyList<FakeSwap>? swaps)
    {
        var mine = (swaps ?? [])
            .Where(s => (string.Equals(s.Preset, presetName, StringComparison.OrdinalIgnoreCase)
                         || (s.Preset == GameFilterKey && s.Section is GameFilter.TcpName or GameFilter.UdpName))
                        && !string.IsNullOrWhiteSpace(s.Blob))
            .ToList();

        if (mine.Count == 0)
            return 0;

        var declare = new List<string>();
        int replaced = 0;
        int start = 0;

        while (start < arguments.Count)
        {
            int end = arguments.IndexOf("--new", start);

            if (end < 0)
                end = arguments.Count;

            var name = arguments.Skip(start).Take(end - start)
                .FirstOrDefault(a => a.StartsWith("--name=", StringComparison.Ordinal))?["--name=".Length..];

            int slot = 0;

            for (int i = start; i < end && name is not null; i++)
            {
                if (!arguments[i].StartsWith("--lua-desync=", StringComparison.Ordinal)
                    || BlobOf(arguments[i]["--lua-desync=".Length..]) is null)
                {
                    continue;
                }

                int here = slot++;
                var swap = mine.FirstOrDefault(s => s.Slot == here && string.Equals(s.Section, name, StringComparison.Ordinal));

                if (swap is null)
                    continue;

                var blob = NameFor(swap.Blob);

                if (blob != swap.Blob && !declare.Contains($"--blob={blob}:@bin/{swap.Blob}"))
                    declare.Add($"--blob={blob}:@bin/{swap.Blob}");

                arguments[i] = "--lua-desync=" + WithBlob(arguments[i]["--lua-desync=".Length..], blob);
                replaced++;
            }

            start = end + 1;
        }

        // Глобальные ключи от места в строке не зависят — в начало, как у game filter.
        arguments.InsertRange(0, declare);

        return replaced;
    }

    /// <summary>Имя образца в строке: встроенный — как есть, файл — своим объявлением.</summary>
    internal static string NameFor(string id) =>
        BuiltIns.Any(b => b.Id == id)
            ? id
            : Prefix + new string(Path.GetFileNameWithoutExtension(id).Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    /// <summary>Значение <c>blob=</c> шага; <c>null</c> — подделки в шаге нет.</summary>
    internal static string? BlobOf(string step) =>
        step.Split(':').Skip(1)
            .Where(p => p.StartsWith("blob=", StringComparison.Ordinal))
            .Select(p => p["blob=".Length..])
            .FirstOrDefault(v => v.Length > 0);

    private static string WithBlob(string step, string blob) =>
        string.Join(':', step.Split(':').Select((p, i) => i > 0 && p.StartsWith("blob=", StringComparison.Ordinal) ? "blob=" + blob : p));

    /// <summary>Объявления <c>--blob=имя:@файл</c>: имя → имя файла.</summary>
    private static Dictionary<string, string> Declarations(IEnumerable<string> globals)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var arg in globals.Where(a => a.StartsWith("--blob=", StringComparison.Ordinal)))
        {
            var body = arg["--blob=".Length..];
            int colon = body.IndexOf(':');

            if (colon > 0 && body.Length > colon + 1 && body[colon + 1] == '@')
                result.TryAdd(body[..colon], Path.GetFileName(body[(colon + 2)..]));
        }

        return result;
    }

    private static bool IsUdp(IEnumerable<string> raw) =>
        raw.Any(a => a.StartsWith("--filter-udp", StringComparison.Ordinal)
                     || (a.StartsWith("--filter-l7=", StringComparison.Ordinal)
                         && a.Split('=', 2)[1].Split(',').Any(p => p is "quic" or "stun" or "discord" or "wireguard" or "dht" or "dtls")));
}
