using System.Globalization;

namespace NetZapret.Zapret;

/// <summary>Что вышло из рецепта секции на языке ByeDPI.</summary>
internal enum ByeDpiRecipeKind
{
    /// <summary><c>pass</c>: пустая группа — имена секции проходят нетронутыми.</summary>
    Pass,

    /// <summary>Рецепт переведён — точно или заменой из <c>byedpi.md</c> §3.3.</summary>
    Translated,

    /// <summary>
    /// Без root не переводится ничего: пустая группа, чтобы имена секции
    /// не съехали к чужому рецепту ниже, а при отказе сработали запасные.
    /// </summary>
    Empty,
}

/// <summary>Рецепт секции в ключах ByeDPI.</summary>
/// <param name="Tokens">Ключи приёмов группы, без отбора (<c>-K</c>, <c>-H</c>, <c>-j</c>).</param>
/// <param name="Lost">Шаги и ключи winws2, которых у ByeDPI без root нет, — для предупреждения.</param>
internal sealed record ByeDpiRecipe(IReadOnlyList<string> Tokens, ByeDpiRecipeKind Kind, IReadOnlyList<string> Lost)
{
    /// <summary>Чем группа отличается от других: одинаковые склеиваются.</summary>
    public string Key => string.Join(' ', Tokens);
}

/// <summary>
/// Перевод шагов <c>--lua-desync</c> в приёмы ByeDPI (<c>byedpi.md</c> §3.3).
/// </summary>
/// <remarks>
/// <para>
/// Дословно переводится только <c>pass</c>. Остальное — замены: у ByeDPI без
/// root нет сырых пакетов, значит нет seqovl, tcp_ts, tcp_ack, autottl,
/// повторов, badsum, send и syndata. Что осталось — разрезы (<c>-s</c>),
/// беспорядок через TTL=1 и повтор ядра (<c>-d</c>), OOB, подделка той же
/// длины с постоянным TTL (<c>-f</c>), разрез записи TLS (<c>-r</c>).
/// </para>
/// <para>
/// Классы V12 Lite и замены (§3.3, вывод для генератора): A и B (hostfakesplit,
/// hostfakesplit_multi) → подделка с белым именем <c>-f-1 -Q orig -n …</c>;
/// C (multidisorder) → многоступенчатый <c>-d/-s</c>, режущий имя с шагом
/// 3–5 байт: ТСПУ режет по подстроке <c>youtube</c> и <c>googlevideo</c>
/// (замер 24.10, CLAUDE.md), и разрез обязан рвать слово; D (fake + multidisorder)
/// → <c>-d1 -f-1 -n &lt;имя образца&gt; -Q rand</c> — строка, державшаяся в мае
/// 2026 у Т2, МТС, Yota и Билайна; F (наложение приветствием 4pda) → подделка
/// с тем же белым именем. E (send + syndata) не переводится: Telegram по адресам
/// десинк не спасает, Discord media и IP fallback — запасной цепочке или VPN.
/// Ни одна замена на телефоне не проверена.
/// </para>
/// </remarks>
internal static class ByeDpiRecipes
{
    /// <summary>Многоступенчатый беспорядок — замена multidisorder (класс C).</summary>
    public const string MultiStage = "-d1 -d3+s -s6+s -d9+s -s12+s -d15+s -s20+s -d25+s -s30+s -d35+s -r1+s";

    /// <summary>Шаги, за которыми стоит автоподбор winws2 по своей памяти, — переносить нечего.</summary>
    private static readonly HashSet<string> Rotation =
        new(StringComparer.OrdinalIgnoreCase) { "circular", "shadow_probe", "tls_aggressive" };

    /// <summary>Шаги, которым без root нет замены вовсе.</summary>
    private static readonly HashSet<string> RawOnly =
        new(StringComparer.OrdinalIgnoreCase) { "send", "syndata", "udplen" };

    /// <summary>Рецепт секции по TCP.</summary>
    public static ByeDpiRecipe Tcp(IReadOnlyList<string> recipes, ByeDpiBlobs blobs, bool md5sig)
    {
        var steps = recipes.Select(Step.Parse).ToList();

        if (steps.Count == 0)
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Empty, []);

        if (steps.All(s => s.Name.Equals("pass", StringComparison.OrdinalIgnoreCase)))
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Pass, []);

        // Пресеты «white sni» и «shadow probes»: секция — это перебор стратегий
        // с памятью winws2 (strategy=N у каждого шага). Разбирать его по шагам
        // значило бы склеить сорок рецептов в один, которого автор не писал.
        // Перебор у ByeDPI свой — запасные группы -A t,s за группой секции.
        if (steps.Any(s => Rotation.Contains(s.Name) || s.Has("strategy")))
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Empty, ["автоподбор winws2 (circular)"]);

        var lost = new List<string>();
        bool md5 = steps.Any(s => s.Has("tcp_md5"));

        foreach (var step in steps.Where(s => RawOnly.Contains(s.Name)))
            lost.Add(step.Name);

        // Подделка с белым именем: hostfakesplit, hostfakesplit_multi, white_seqovl
        // и наложение приветствием (seqovl_pattern с образцом TLS). Фильтр должен
        // увидеть чужое имя — ByeDPI покажет его подделкой той же длины, что
        // настоящий ClientHello (-Q orig), а повтор ядро отправит уже с настоящими
        // байтами. ТСПУ сверяет длину подделки с настоящей (ntc.party №1279–1285),
        // поэтому orig, а не готовый образец.
        var white = new List<string>();

        foreach (var step in steps)
        {
            switch (step.Name.ToLowerInvariant())
            {
                case "hostfakesplit":
                    white.Add(step.Value("host") ?? "www.google.com");
                    break;

                case "hostfakesplit_multi":
                    white.AddRange((step.Value("hosts") ?? "www.google.com")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "white_seqovl":
                    white.Add(step.Value("sni") ?? "www.google.com");
                    break;

                case "multisplit" or "multidisorder" when step.Value("seqovl_pattern") is { } pattern
                        && blobs.Sni(pattern) is { } sni:
                    white.Add(sni);
                    break;
            }
        }

        var tokens = new List<string>();

        if (white.Count > 0)
        {
            tokens.AddRange(["-f-1", "-Q", "orig"]);

            foreach (var name in white.Distinct(StringComparer.OrdinalIgnoreCase))
                tokens.AddRange(["-n", name]);
        }
        else
        {
            var fake = steps.FirstOrDefault(s => s.Name.Equals("fake", StringComparison.OrdinalIgnoreCase));
            var fakeTokens = fake is null ? null : FakeTls(fake, blobs);

            if (steps.Any(s => s.Name.Equals("multidisorder", StringComparison.OrdinalIgnoreCase)))
            {
                // D: fake + multidisorder; без подделки — многоступенчатый C.
                if (fakeTokens is not null)
                    tokens.AddRange(["-d1", .. fakeTokens]);
                else
                    tokens.AddRange(Split(MultiStage));
            }
            else
            {
                foreach (var step in steps)
                {
                    switch (step.Name.ToLowerInvariant())
                    {
                        case "multisplit":
                            foreach (var pos in Positions(step.Value("pos") ?? "2", lost))
                                tokens.Add("-s" + pos);
                            break;

                        case "fakedsplit":
                            foreach (var pos in Positions(step.Value("pos") ?? "1", lost).Take(1))
                                tokens.Add("-f" + pos);
                            break;

                        case "fakeddisorder":
                            tokens.Add("-d1");

                            foreach (var pos in Positions(step.Value("pos") ?? "1", lost).Take(1))
                                tokens.Add("-f" + pos);
                            break;

                        case "tls_multisplit_sni":
                            // Разрез по имени — да, наложение — нет; разрез записи
                            // TLS (-r1+s) — замена наложению из §3.3.
                            tokens.AddRange(["-s0+s", "-s0+sm", "-r1+s"]);
                            break;
                    }
                }

                if (fakeTokens is not null)
                    tokens.AddRange(fakeTokens);
            }
        }

        foreach (var step in steps)
        {
            var name = step.Name.ToLowerInvariant();

            if (name is "pass" or "fake" or "multisplit" or "multidisorder" or "fakedsplit" or "fakeddisorder"
                    or "tls_multisplit_sni" or "hostfakesplit" or "hostfakesplit_multi" or "white_seqovl"
                || RawOnly.Contains(name))
            {
                continue;
            }

            lost.Add(step.Name);
        }

        if (tokens.Count == 0)
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Empty, lost);

        // -S действует только на куски -f (desync.c), а на ядре без
        // CONFIG_TCP_MD5SIG рвёт соединение — ставится, лишь когда хозяин
        // проверил ядро (ByeDpiRequest.Md5Sig).
        if (md5 && md5sig && tokens.Any(t => t.StartsWith("-f", StringComparison.Ordinal)))
            tokens.Add("-S");

        return new ByeDpiRecipe(tokens, ByeDpiRecipeKind.Translated, lost);
    }

    /// <summary>Рецепт секции по UDP: подделки перед первой датаграммой (<c>-a</c>, <c>-l</c>).</summary>
    public static ByeDpiRecipe Udp(IReadOnlyList<string> recipes, ByeDpiBlobs blobs)
    {
        var steps = recipes.Select(Step.Parse).ToList();

        if (steps.Count == 0)
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Empty, []);

        if (steps.All(s => s.Name.Equals("pass", StringComparison.OrdinalIgnoreCase)))
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Pass, []);

        if (steps.Any(s => Rotation.Contains(s.Name) || s.Has("strategy")))
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Empty, ["автоподбор winws2 (circular)"]);

        var lost = steps
            .Where(s => !s.Name.Equals("fake", StringComparison.OrdinalIgnoreCase)
                && !s.Name.Equals("pass", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();

        var fakes = steps.Where(s => s.Name.Equals("fake", StringComparison.OrdinalIgnoreCase)).ToList();

        if (fakes.Count == 0)
            return new ByeDpiRecipe([], ByeDpiRecipeKind.Empty, lost);

        int repeats = fakes.Max(f => int.TryParse(f.Value("repeats"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 1);
        var tokens = new List<string> { "-a", repeats.ToString(CultureInfo.InvariantCulture) };

        // Образец — файлом (-l): подделка «из файла, иначе 64 нулевых байта».
        // Встроенные образцы winws2 (fake_default_*) и строки 0x… файла не имеют —
        // тогда нули, что для 0x00… и есть то же самое.
        if (fakes.Select(f => f.Value("blob")).FirstOrDefault(b => b is not null) is { } blob && blobs.File(blob) is { } file)
            tokens.AddRange(["-l", file]);

        return new ByeDpiRecipe(tokens, ByeDpiRecipeKind.Translated, lost);
    }

    /// <summary>
    /// Подделка TLS из шага <c>fake</c>: <c>-f-1</c>, имя образца и случайные поля.
    /// </summary>
    /// <remarks>
    /// Одна подделка длиной в настоящий ClientHello (ByeDPI подгоняет размер);
    /// repeats, ip_autottl, tcp_ack и badsum без root недоступны, TTL постоянный
    /// (по умолчанию 8). Образец не TLS (QUIC, HTTP, нули) — своя подделка ByeDPI.
    /// </remarks>
    private static List<string> FakeTls(Step fake, ByeDpiBlobs blobs)
    {
        var tokens = new List<string> { "-f-1" };

        if (fake.Value("blob") is { } blob && blobs.Sni(blob) is { } sni)
            tokens.AddRange(["-n", sni, "-Q", "rand"]);

        return tokens;
    }

    /// <summary>
    /// Позиции winws2 (<c>1</c>, <c>host+2</c>, <c>midsld</c>, <c>sniext+1</c>, <c>endhost-2</c>)
    /// в позиции ByeDPI (<c>1</c>, <c>2+s</c>, <c>0+sm</c>, <c>-4+s</c>, <c>-2+se</c>).
    /// </summary>
    /// <remarks>
    /// У ByeDPI нет метки SLD — <c>sld</c> и <c>midsld</c> идут в середину имени
    /// (<c>+sm</c>), <c>endsld</c> — в конец (<c>+se</c>). <c>sniext</c> — начало
    /// данных расширения SNI, пятью байтами раньше имени (§3.3). Порядок
    /// сохраняется: позиции у ByeDPI обязаны идти по возрастанию, иначе «split
    /// cancel» и остаток уходит одним куском, — а вычислить их заранее, без
    /// пакета, нельзя.
    /// </remarks>
    private static IEnumerable<string> Positions(string list, List<string> lost)
    {
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Position(raw) is { } pos)
                yield return pos;
            else
                lost.Add($"pos={raw}");
        }
    }

    private static string? Position(string marker)
    {
        if (int.TryParse(marker, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var absolute))
            return absolute.ToString(CultureInfo.InvariantCulture);

        int sign = marker.IndexOfAny(['+', '-']);
        var name = (sign < 0 ? marker : marker[..sign]).ToLowerInvariant();
        int offset = 0;

        if (sign >= 0 && !int.TryParse(marker[sign..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset))
            return null;

        string Rel(int value, string flag) => value.ToString(CultureInfo.InvariantCulture) + flag;

        return name switch
        {
            "host" => Rel(offset, "+s"),
            "endhost" => Rel(offset, "+se"),
            "midsld" or "sld" => Rel(offset, "+sm"),
            "endsld" => Rel(offset, "+se"),
            "sniext" => Rel(offset - 5, "+s"),
            "method" => offset.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    public static IReadOnlyList<string> Split(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Шаг <c>имя:ключ=значение:флаг</c>.</summary>
    private sealed record Step(string Name, IReadOnlyDictionary<string, string> Parameters)
    {
        public static Step Parse(string recipe)
        {
            var parts = recipe.Split(':');
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in parts.Skip(1))
            {
                int eq = part.IndexOf('=');

                if (eq < 0)
                    parameters[part.Trim()] = string.Empty;
                else
                    parameters[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }

            return new Step(parts[0].Trim(), parameters);
        }

        public bool Has(string key) => Parameters.ContainsKey(key);

        public string? Value(string key) =>
            Parameters.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    }
}

/// <summary>
/// Образцы пакетов пресета (<c>--blob=имя:@bin/файл</c>): файл и имя в ClientHello.
/// </summary>
internal sealed class ByeDpiBlobs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _sni = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _zapretRoot;

    public ByeDpiBlobs(IEnumerable<string> globalArguments, string zapretRoot)
    {
        _zapretRoot = zapretRoot;

        foreach (var argument in globalArguments)
        {
            if (!argument.StartsWith("--blob=", StringComparison.OrdinalIgnoreCase))
                continue;

            var body = argument["--blob=".Length..];
            int colon = body.IndexOf(':');

            if (colon > 0)
                _values[body[..colon].Trim()] = body[(colon + 1)..].Trim();
        }
    }

    /// <summary>Полный путь к файлу образца; <c>null</c> — образец не файлом или файла нет.</summary>
    public string? File(string blob)
    {
        if (!_values.TryGetValue(blob, out var value) || !value.StartsWith('@'))
            return null;

        var path = Path.GetFullPath(Path.Combine(_zapretRoot, value[1..].Replace('/', Path.DirectorySeparatorChar)));
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Имя сервера в образце-ClientHello; <c>null</c> — образец не TLS или без имени.
    /// </summary>
    /// <remarks>
    /// Читается из самого файла, а не угадывается по имени: у tls_clienthello_5.bin
    /// по имени файла имени сервера не узнать, а «vk_com_kyber» угадалось бы
    /// неверно.
    /// </remarks>
    public string? Sni(string blob)
    {
        if (_sni.TryGetValue(blob, out var known))
            return known;

        string? sni = null;

        try
        {
            if (File(blob) is { } path)
                sni = ClientHelloSni(System.IO.File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _sni[blob] = sni;
        return sni;
    }

    /// <summary>Имя из расширения server_name в записи TLS с ClientHello.</summary>
    internal static string? ClientHelloSni(byte[] data)
    {
        // Запись: тип 0x16, версия (2), длина (2); сообщение: тип 1, длина (3),
        // версия (2), random (32), session_id, cipher_suites, compression,
        // расширения.
        if (data.Length < 43 || data[0] != 0x16 || data[5] != 0x01)
            return null;

        int p = 5 + 4 + 2 + 32;

        if (!Skip(data, ref p, 1) || !Skip(data, ref p, 2) || !Skip(data, ref p, 1) || p + 2 > data.Length)
            return null;

        int end = Math.Min(data.Length, p + 2 + (data[p] << 8 | data[p + 1]));
        p += 2;

        while (p + 4 <= end)
        {
            int type = data[p] << 8 | data[p + 1];
            int length = data[p + 2] << 8 | data[p + 3];
            p += 4;

            if (type == 0 && p + 5 <= end)
            {
                int nameLength = data[p + 3] << 8 | data[p + 4];

                if (data[p + 2] != 0 || p + 5 + nameLength > end || nameLength == 0)
                    return null;

                var name = System.Text.Encoding.ASCII.GetString(data, p + 5, nameLength).ToLowerInvariant();
                return name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') ? name : null;
            }

            p += length;
        }

        return null;

        static bool Skip(byte[] bytes, ref int position, int lengthBytes)
        {
            if (position + lengthBytes > bytes.Length)
                return false;

            int length = 0;

            for (int i = 0; i < lengthBytes; i++)
                length = length << 8 | bytes[position + i];

            position += lengthBytes + length;
            return position <= bytes.Length;
        }
    }
}
