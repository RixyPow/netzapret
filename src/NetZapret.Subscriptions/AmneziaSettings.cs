using System.Globalization;
using System.Text.Json.Nodes;

namespace NetZapret.Subscriptions;

/// <summary>
/// Маскировка AmneziaWG — из ссылки или файла <c>.conf</c> в блок <c>amnezia</c> sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Типы проверены на нашем движке (1.14.1-extended-2.7.2, <c>sing-box check</c>,
/// 01.10): <c>h1</c>–<c>h4</c> он принимает и числом, и строкой-диапазоном
/// («100-200», так их пишет AmneziaWG 2.0), <c>i1</c>–<c>i5</c> — строками,
/// а <c>jc</c>, <c>jmin</c>, <c>jmax</c>, <c>s1</c>–<c>s4</c> — только числом:
/// <c>"jc": "4"</c> в кавычках роняет весь конфиг, а с ним и все остальные
/// серверы. Поэтому нечисловое значение здесь — ошибка разбора этого ключа,
/// а не строка в конфиге.
/// </para>
/// <para>
/// Ключи в ссылке и в файле те же, что у самой AmneziaWG (<c>Jc</c>,
/// <c>H1</c>, <c>I1</c>), регистр не важен.
/// </para>
/// </remarks>
public static class AmneziaSettings
{
    private static readonly string[] Numbers = ["jc", "jmin", "jmax", "s1", "s2", "s3", "s4"];
    private static readonly string[] Headers = ["h1", "h2", "h3", "h4"];
    private static readonly string[] Signatures = ["i1", "i2", "i3", "i4", "i5"];

    /// <summary>
    /// Параметры маскировки, которых движок не знает: J1–J3 и Itime у AmneziaWG 1.5
    /// и всё, что похоже на параметр маскировки, но не из наших списков.
    /// </summary>
    /// <remarks>
    /// Прежде такие ключи отбрасывались молча, и сервер с ними «не отвечал» без
    /// единого слова о причине (обсуждение №40, 28.10). Параметры AmneziaWG 3.0
    /// и 3.1 сюда больше не входят: движок 2.7.3 их знает (см. <see cref="Generation3"/>).
    /// </remarks>
    public static IReadOnlyList<string> Unsupported(IEnumerable<string> keys) =>
        keys.Where(k => LooksAmnezia.IsMatch(k)
                && Generation3Name(k) is null
                && !Numbers.Concat(Headers).Concat(Signatures).Contains(k, StringComparer.OrdinalIgnoreCase))
            .Select(Display)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Параметр AmneziaWG 3.0/3.1 — в CamelCase файла или через «_», как в JSON AmneziaVPN.</summary>
    public static bool IsGeneration3(string key) => Generation3Name(key) is not null;

    /// <summary>
    /// Оговорка к серверу WireGuard/AmneziaWG по ключам, которых движок не знает;
    /// <c>null</c> — говорить не о чем.
    /// </summary>
    /// <remarks>
    /// Ключ защиты заголовков движок принимает, но на опыте 05.11 (два sing-box 2.7.3
    /// на петле, ключи и маскировка одинаковые) рукопожатие с ним не проходило ни разу:
    /// сервер писал «received message with unknown type». Без ключа, с дополнением,
    /// хвостами и таймерами — проходило за 0,05–0,16 с. С настоящим сервером
    /// AmneziaWG 3 не проверено — может и пройти, поэтому ключ в конфиг идёт.
    /// </remarks>
    public static string? Note(IReadOnlyDictionary<string, string> extra)
    {
        var parts = new List<string>();

        if (extra.Any(p => Generation3Name(p.Key) is "HeaderProtectionKey" && !string.IsNullOrWhiteSpace(p.Value)))
        {
            parts.Add("защиту заголовков AmneziaWG 3 (HeaderProtectionKey) движок принимает, но на нашей проверке "
                + "рукопожатие с ней не проходило — сервер может не ответить");
        }

        var older = Unsupported(extra.Keys);

        if (older.Count > 0)
            parts.Add($"параметров AmneziaWG {string.Join(", ", older)} движок не знает — сервер может не ответить");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static bool? Switch(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => null,
    };

    /// <summary>Каноническое имя параметра AmneziaWG 3.x; не из них — null.</summary>
    /// <remarks>В файле — CamelCase, во внутреннем интерфейсе и JSON — через подчёркивание.</remarks>
    private static string? Generation3Name(string key)
    {
        var bare = key.Replace("_", string.Empty, StringComparison.Ordinal);

        return Generation3.Select(g => g.Name).FirstOrDefault(n => n.Equals(bare, StringComparison.OrdinalIgnoreCase));
    }

    private static string Display(string key) =>
        Generation3Name(key)
            ?? (key.Length <= 2 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant());

    private enum Kind { Text, Range, Switch }

    /// <summary>
    /// Параметры AmneziaWG 3.0 (24.07.2026) и 3.1 (12.08) — имя в файле, имя в блоке
    /// <c>amnezia</c> движка, вид значения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сборка extended знает их с 2.7.0 (04.09, «Integrate Amnezia 3.1»):
    /// <c>option/wireguard.go</c> тега 2.7.3 и пример <c>examples/amnezia</c>,
    /// имена есть и в нашем <c>sing-box.exe</c> (сверка 05.11). 29.10 я записал
    /// обратное — «движок знает только 2.0» — и людям с AmneziaWG 3 программа
    /// говорила «сервер не ответит», а ключ защиты в конфиг не шёл. Ошибка была
    /// моя: сверял строки движка по старому списку и новых имён не искал.
    /// </para>
    /// <para>
    /// Диапазоны (<c>badoption.Range[uint32]</c>) движок пишет «50-100», как
    /// <c>h1</c>–<c>h4</c>; одно число — тоже.
    /// </para>
    /// </remarks>
    private static readonly (string Name, string Json, Kind Kind)[] Generation3 =
    [
        ("HeaderProtectionKey", "header_protection_key", Kind.Text),
        ("ContentPaddingAddition", "content_padding_addition", Kind.Range),
        ("RekeyAfterTime", "rekey_after_time", Kind.Range),
        ("RekeyTimeout", "rekey_timeout", Kind.Range),
        ("RejectAfterTime", "reject_after_time", Kind.Range),
        ("KeepaliveTimeout", "keepalive_timeout", Kind.Range),
        ("MaxHandshakeAttempts", "max_handshake_attempts", Kind.Range),
        ("RandomTrailers", "random_trailers", Kind.Switch),
        ("DisableCookies", "disable_cookies", Kind.Switch),
    ];

    private static readonly System.Text.RegularExpressions.Regex RangeText = new(
        @"^\d+(?:\s*-\s*\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex LooksAmnezia = new(
        @"^(?:[jshi]\d+|jc|jmin|jmax|itime)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <param name="get">Значение по имени ключа без учёта регистра, <c>null</c> — нет такого.</param>
    /// <returns>Блок JSON строкой; <c>null</c>, если маскировки нет — обычный WireGuard.</returns>
    /// <exception cref="FormatException">Числовой ключ задан не числом.</exception>
    public static string? From(Func<string, string?> get)
    {
        var block = new JsonObject();

        foreach (var key in Numbers)
        {
            var text = get(key)?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"параметр AmneziaWG {key.ToUpperInvariant()} = «{text}» — не число");

            block[key] = number;
        }

        foreach (var key in Headers)
        {
            var text = get(key)?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            // До 2^32: у AmneziaWG заголовки беззнаковые 32-битные, в int не влезают.
            block[key] = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : text;
        }

        foreach (var key in Signatures)
        {
            var text = get(key)?.Trim();

            if (!string.IsNullOrEmpty(text))
                block[key] = text;
        }

        // Третье поколение: в файле CamelCase, в ссылке и JSON AmneziaVPN — через «_».
        foreach (var (name, json, kind) in Generation3)
        {
            var text = (get(name) ?? get(json))?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            switch (kind)
            {
                case Kind.Text:
                    block[json] = text;
                    break;

                // Кривой диапазон роняет весь конфиг, а с ним и прочие серверы, —
                // поэтому, как и у чисел выше, это ошибка разбора этого сервера.
                case Kind.Range when !RangeText.IsMatch(text):
                    throw new FormatException($"параметр AmneziaWG {name} = «{text}» — не число и не диапазон");

                case Kind.Range:
                    block[json] = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                        ? number
                        : text.Replace(" ", string.Empty, StringComparison.Ordinal);
                    break;

                case Kind.Switch:
                    if (Switch(text) is not { } on)
                        throw new FormatException($"параметр AmneziaWG {name} = «{text}» — не on/off");

                    if (on)
                        block[json] = true;
                    break;
            }
        }

        // С ключом защиты движок требует S3 и S4 больше 12 и иначе не поднимается
        // вовсе — со всеми серверами (опыт 05.11: «S3 must be more then 12 to use
        // headerProtection»; sing-box check это пропускает). Отказ — этому серверу.
        if (block.ContainsKey("header_protection_key"))
        {
            foreach (var key in new[] { "s3", "s4" })
            {
                if (!(block[key] is JsonValue v && v.TryGetValue<int>(out var size) && size > 12))
                    throw new FormatException($"защите заголовков AmneziaWG нужен {key.ToUpperInvariant()} больше 12 — иначе движок не поднимется");
            }
        }

        return block.Count == 0 ? null : block.ToJsonString();
    }
}
