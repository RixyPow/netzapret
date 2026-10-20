using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NetZapret.Proxy;

/// <summary>
/// Что ещё скачать с сайта, когда главная страница слишком мала, чтобы
/// показать заморозку.
/// </summary>
/// <remarks>
/// <para>
/// Заморозка ТСПУ встаёт на 12–20 КБ одного соединения
/// (<see cref="Signatures.FreezeFrom"/>, <see cref="Signatures.FreezeTo"/>),
/// а проба мерит главную страницу. У 7tv.io она 148 Б и проходит, тогда
/// как ответ API на 112 КБ замерзает на 0–3,6 КБ, 15 из 15 (замер 19.10).
/// Сайт звался доступным и не открывался.
/// </para>
/// <para>
/// Браузер за маленькой страницей тянет её скрипты и стили, и первый же
/// большой из них попадает под заморозку. Так же делает и проба: берёт из
/// разметки скрипт или стиль той же зоны и качает его отдельным
/// соединением — счётчик заморозки на новом соединении обнуляется
/// (ntc.party, «Zapret2», №1329, 11.10), и подресурс должен превысить
/// порог сам. Тот же приём у Ladon 3.1 (03.10).
/// </para>
/// <para>
/// Только своя зона: чужой хост — другое имя со своим вердиктом,
/// и обрыв на нём говорил бы не об этом сайте.
/// </para>
/// </remarks>
internal static partial class PageAssets
{
    /// <summary>Сколько подресурсов пробовать, прежде чем сдаться.</summary>
    /// <remarks>
    /// Два: первый скрипт бывает крошечной подгрузкой, второй — уже сборка.
    /// Больше — лишние соединения на каждое из полутора сотен имён.
    /// </remarks>
    public const int Tries = 2;

    /// <summary>Потолок разобранной разметки: ссылки стоят в начале страницы.</summary>
    private const int DecodedLimit = 1024 * 1024;

    /// <summary>
    /// Пути скриптов и стилей той же зоны: сборки, прочие скрипты, стили.
    /// </summary>
    /// <remarks>
    /// Порядок — по ожидаемому размеру, потому что проб всего <see cref="Tries"/>.
    /// У www.hetzner.com первые два скрипта в разметке — matomo.js на 372 Б
    /// и его менеджер меток, а vendors.js на 154 КБ стоит восьмым (20.10):
    /// в порядке разметки заморозку не поймать. Сборка узнаётся по имени
    /// файла (<see cref="BundleWords"/>). Стили — последними: они меньше.
    /// Только https-ссылки на тот же хост либо относительные.
    /// </remarks>
    public static IReadOnlyList<string> SameSite(string host, string html)
    {
        var scripts = new List<string>();
        var styles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in ScriptSrc().Matches(html))
            Add(host, m.Groups[1].Value, scripts, seen);

        foreach (Match m in LinkTag().Matches(html))
        {
            var tag = m.Value;
            var href = Href().Match(tag);

            if (!href.Success)
                continue;

            // Предзагрузка модуля — тоже скрипт: так грузят сборку SvelteKit
            // и Vite, у 7tv.app в разметке нет ни одного <script src> своей
            // зоны, только modulepreload (20.10).
            if (ModulePreloadRel().IsMatch(tag))
                Add(host, href.Groups[1].Value, scripts, seen);
            else if (StylesheetRel().IsMatch(tag))
                Add(host, href.Groups[1].Value, styles, seen);
        }

        return
        [
            .. scripts.Where(LooksLikeBundle),
            .. scripts.Where(p => !LooksLikeBundle(p)),
            .. styles,
        ];
    }

    /// <summary>Слова в пути, по которым скрипт похож на сборку.</summary>
    private static readonly string[] BundleWordsInPath =
        ["bundle", "chunk", "runtime", "framework", "entry"];

    /// <summary>
    /// Слова только в имени файла: в папках «app» и «vendor» лежит что угодно
    /// (у www.hetzner.com в <c>/_resources/vendor/…</c> — i18n.js на 875 Б).
    /// </summary>
    private static readonly string[] BundleWordsInName = ["vendor", "app", "main", "index"];

    internal static bool LooksLikeBundle(string path)
    {
        var file = path.Split('?')[0];
        var name = file[(file.LastIndexOf('/') + 1)..];

        return BundleWordsInPath.Any(w => file.Contains(w, StringComparison.OrdinalIgnoreCase))
            || BundleWordsInName.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static void Add(string host, string raw, List<string> found, HashSet<string> seen)
    {
        var path = SameSitePath(host, raw);

        if (path is not null && seen.Add(path))
            found.Add(path);
    }

    /// <summary>
    /// Путь ссылки, если она ведёт на тот же хост; иначе <c>null</c>.
    /// </summary>
    internal static string? SameSitePath(string host, string raw)
    {
        var text = System.Net.WebUtility.HtmlDecode(raw.Trim());

        if (text.Length == 0 || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith('#'))
        {
            return null;
        }

        // Ссылка без схемы — «//cdn.example.com/a.js» — это чужой или свой
        // хост по той же схеме, что страница; у нас страница всегда https.
        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "https:" + text;

        if (!Uri.TryCreate(new Uri($"https://{host}/"), text, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.PathAndQuery == "/" ? null : uri.PathAndQuery;
    }

    /// <summary>
    /// Тело ответа как текст: собранное из кусков и распакованное.
    /// </summary>
    /// <returns><c>null</c>, если тело не разобрать — тогда подресурсов не будет.</returns>
    /// <remarks>
    /// Проба просит сжатие, как браузер (см. <see cref="BlockCheck"/>), и тело
    /// приходит gzip, deflate или brotli. Разбирается только ради ссылок;
    /// ошибка распаковки — не поломка сайта, а повод обойтись без подресурса.
    /// </remarks>
    public static string? Decode(byte[] body, bool chunked, string? contentEncoding)
    {
        try
        {
            var raw = chunked ? Unchunk(body) : body;

            if (raw is null)
                return null;

            var bytes = Inflate(raw, contentEncoding);

            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Склеивает тело из кусков «размер\r\nданные\r\n».
    /// </summary>
    /// <returns><c>null</c> при испорченной разметке кусков.</returns>
    internal static byte[]? Unchunk(byte[] body)
    {
        var output = new MemoryStream();
        int pos = 0;

        while (true)
        {
            int lineEnd = IndexOfCrlf(body, pos);

            if (lineEnd < 0)
                return null;

            var line = Encoding.ASCII.GetString(body, pos, lineEnd - pos);
            var sizeText = line.Split(';')[0].Trim();

            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                return null;

            pos = lineEnd + 2;

            if (size == 0)
                break;

            if (pos + size > body.Length)
            {
                // Последний кусок не дошёл целиком — берём, что есть: ссылки
                // стоят в начале, а дальше читать уже некому.
                output.Write(body, pos, body.Length - pos);
                break;
            }

            output.Write(body, pos, size);
            pos += size;

            // После данных куска положен CRLF; его может не быть, если тело
            // оборвано ровно на границе.
            if (pos + 2 <= body.Length && body[pos] == '\r' && body[pos + 1] == '\n')
                pos += 2;
        }

        return output.ToArray();
    }

    private static int IndexOfCrlf(byte[] body, int from)
    {
        for (int i = from; i + 1 < body.Length; i++)
        {
            if (body[i] == '\r' && body[i + 1] == '\n')
                return i;
        }

        return -1;
    }

    private static byte[]? Inflate(byte[] raw, string? contentEncoding)
    {
        var encoding = contentEncoding?.Trim().ToLowerInvariant() ?? "";

        using var input = new MemoryStream(raw);

        Stream? decoder = encoding switch
        {
            "" or "identity" => null,
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),

            // «deflate» в HTTP — это zlib с заголовком, но часть серверов
            // шлёт голый deflate; пробуются оба.
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            _ => null,
        };

        if (encoding.Length > 0 && encoding != "identity" && decoder is null)
            return null;

        if (decoder is null)
            return raw.Length <= DecodedLimit ? raw : raw[..DecodedLimit];

        try
        {
            using (decoder)
            {
                return ReadUpTo(decoder, DecodedLimit);
            }
        }
        catch (InvalidDataException) when (encoding == "deflate")
        {
            using var again = new MemoryStream(raw);
            using var bare = new DeflateStream(again, CompressionMode.Decompress);

            return ReadUpTo(bare, DecodedLimit);
        }
    }

    private static byte[] ReadUpTo(Stream stream, int limit)
    {
        var output = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (output.Length < limit)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - output.Length));

            if (read <= 0)
                break;

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>Ссылка скрипта одной группой, в какие бы кавычки ни была взята.</summary>
    [GeneratedRegex(@"<script\b[^>]*?\ssrc\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptSrc();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"\srel\s*=\s*[""']?[^""'>]*\bstylesheet\b", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetRel();

    [GeneratedRegex(@"\srel\s*=\s*[""']?[^""'>]*\bmodulepreload\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModulePreloadRel();

    [GeneratedRegex(@"\shref\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Href();
}
