using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace NetZapret.Gui;

/// <summary>
/// Значки сайтов, взятые у самих сайтов.
/// </summary>
/// <remarks>
/// <para>
/// У самих, а не у службы значков Google или подобной. Такая служба получила бы
/// список имён, которые человек настраивает в средстве обхода блокировок, —
/// то есть перечень того, что у него закрыто и чем он пользуется. Удобство
/// показа такой цены не стоит.
/// </para>
/// <para>
/// Отсюда и особенность: значок закрытого сайта не загрузится, пока обход
/// выключен. Это честно — мы ходим тем же путём, что и браузер, и если
/// не дошли, то не дошли. Вместо значка остаётся буква.
/// </para>
/// <para>
/// Спрашивается в два захода: сначала <c>/favicon.ico</c>, а если там пусто —
/// то, что страница объявляет в <c>link rel="icon"</c>. Одного первого мало:
/// Figma по этому пути отвечает 404 и объявляет значок в разметке, Miro
/// держит его вообще на чужом имени. Замер 2026-09-08.
/// </para>
/// <para>
/// Кэш на диске, потому что просить одно и то же при каждом открытии раздела
/// значит гонять сеть впустую. Мёртвые ответы тоже запоминаются: имя без
/// значка иначе опрашивалось бы вечно.
/// </para>
/// </remarks>
public static class SiteIcons
{
    private static readonly Dictionary<string, BitmapImage?> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static string Folder => Path.Combine("runtime", "icons");

    /// <summary>
    /// Уже загруженный значок, без единого обращения куда бы то ни было.
    /// </summary>
    /// <remarks>
    /// Раздел с маршрутами пересоздаётся при каждом заходе, и без этого он
    /// каждый раз начинал бы с букв, а значки проступали бы по одному
    /// заново — при живом кэше и на диске, и в памяти. Мигание на ровном
    /// месте: данные есть, а показываются так, будто их нет.
    /// </remarks>
    public static BitmapImage? Cached(string host)
    {
        lock (Loaded)
            return Loaded.GetValueOrDefault(host);
    }

    /// <summary>Спрашивали ли уже про это имя.</summary>
    public static bool Known(string host)
    {
        lock (Loaded)
            return Loaded.ContainsKey(host);
    }

    /// <summary>
    /// Значок сайта; <c>null</c> — нет, и просить больше не будем.
    /// </summary>
    /// <remarks>
    /// Не бросает никогда: раздел с маршрутами должен открыться и без сети.
    /// </remarks>
    public static async Task<BitmapImage?> ForAsync(string host, CancellationToken cancellationToken)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(host, out var known))
                return known;
        }

        var image = FromDisk(host) ?? await FetchAsync(host, cancellationToken);

        lock (Loaded)
            Loaded[host] = image;

        return image;
    }

    private static BitmapImage? FromDisk(string host)
    {
        var path = Path.Combine(Folder, Safe(host) + ".ico");

        if (!File.Exists(path))
            return null;

        // Пустышка означает «спрашивали, значка нет». Без неё имя без значка
        // опрашивалось бы при каждом открытии раздела.
        return new FileInfo(path).Length == 0 ? null : Decode(File.ReadAllBytes(path));
    }

    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    /// <summary>
    /// Спрашивает значок у сайта.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сначала <c>/favicon.ico</c>: так его кладут чаще всего, и одного
    /// запроса хватает. Если там пусто или лежит не картинка — читается сама
    /// страница и берётся то, что она объявляет в <c>link rel="icon"</c>.
    /// </para>
    /// <para>
    /// Второй заход появился не от полноты, а по замеру 2026-09-08: у Figma
    /// по этому пути 404, а значок объявлен тремя строками ниже в разметке;
    /// у Miro он и вовсе лежит на чужом имени. У Reddit, GitHub и Spotify
    /// первого захода хватает — их-то я и проверил, прежде чем писать второй.
    /// </para>
    /// </remarks>
    private static async Task<BitmapImage?> FetchAsync(string host, CancellationToken cancellationToken)
    {
        byte[] bytes = [];
        BitmapImage? image = null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.Add("User-Agent", Agent);

            (image, bytes) = await TryAsync(http, $"https://{host}/favicon.ico", cancellationToken);

            if (image is null)
            {
                foreach (var url in await DeclaredAsync(http, host, cancellationToken))
                {
                    (image, bytes) = await TryAsync(http, url, cancellationToken);

                    if (image is not null)
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Закрыт, молчит, значка нет — все три случая означают одно:
            // показывать нечего. Различать их здесь незачем, этим занята
            // проверка блокировок.
        }

        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(Path.Combine(Folder, Safe(host) + ".ico"), bytes);
        }
        catch (IOException)
        {
        }

        return image;
    }

    /// <summary>Забирает и разбирает; пусто — значит не вышло, причина неважна.</summary>
    private static async Task<(BitmapImage? Image, byte[] Bytes)> TryAsync(
        HttpClient http,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await http.GetByteArrayAsync(url, cancellationToken);
            var image = Decode(body);

            return image is null ? (null, []) : (image, body);
        }
        catch (Exception)
        {
            return (null, []);
        }
    }

    /// <summary>Один тег link из разметки страницы.</summary>
    private static readonly Regex LinkTag = new(
        "<link\\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Attribute = new(
        "(?<name>rel|href)\\s*=\\s*(\"(?<value>[^\"]*)\"|'(?<value>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Значки, которые страница объявляет сама.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Читается только начало страницы: объявления живут в head, а тянуть
    /// ради них мегабайт разметки незачем.
    /// </para>
    /// <para>
    /// Объявленный адрес может вести на чужое имя — сайты кладут значок
    /// на свою же раздачу. Это допускается: страницу мы уже запросили,
    /// и переход по её собственной ссылке ничего нового о человеке
    /// не сообщает. Запрещено другое — спрашивать значок у службы вроде
    /// Google, которой пришлось бы отдать весь список имён.
    /// </para>
    /// <para>
    /// SVG пропускается: WPF его не разбирает, и такой ответ засчитался бы
    /// за неудачу, оборвав перебор на первом же кандидате.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> DeclaredAsync(
        HttpClient http,
        string host,
        CancellationToken cancellationToken)
    {
        string page;

        try
        {
            using var response = await http.GetAsync(
                $"https://{host}/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var head = new byte[64 * 1024];
            int read = await stream.ReadAtLeastAsync(head, head.Length, false, cancellationToken);

            page = System.Text.Encoding.UTF8.GetString(head, 0, read);
        }
        catch (Exception)
        {
            return [];
        }

        var site = new Uri($"https://{host}/");
        var found = new List<(int Rank, string Url)>();

        foreach (Match tag in LinkTag.Matches(page))
        {
            string rel = string.Empty;
            string href = string.Empty;

            foreach (Match attribute in Attribute.Matches(tag.Value))
            {
                if (attribute.Groups["name"].Value.Equals("rel", StringComparison.OrdinalIgnoreCase))
                    rel = attribute.Groups["value"].Value;
                else
                    href = attribute.Groups["value"].Value;
            }

            if (href.Length == 0 || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                continue;

            if (href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(site, href, out var absolute))
                continue;

            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                continue;

            // Обычный значок вперёд: он мельче и рисуется в наших двадцать два
            // пикселя чище, чем apple-touch-icon на сто восемьдесят.
            int rank = rel.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            found.Add((rank, absolute.ToString()));
        }

        return found
            .OrderBy(x => x.Rank)
            .Select(x => x.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            // Сайты отдают под именем favicon.ico что угодно, включая страницу
            // с ошибкой. Не разобралось — значит значка нет.
            return null;
        }
    }

    private static string Safe(string host) =>
        string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));
}
