using System.IO;
using System.Net.Http;
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

    private static async Task<BitmapImage?> FetchAsync(string host, CancellationToken cancellationToken)
    {
        byte[] bytes = [];

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            bytes = await http.GetByteArrayAsync($"https://{host}/favicon.ico", cancellationToken);
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

        return bytes.Length == 0 ? null : Decode(bytes);
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
