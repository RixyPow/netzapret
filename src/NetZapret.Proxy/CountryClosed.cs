namespace NetZapret.Proxy;

/// <summary>
/// Нейросети, которые отказывают России по стране: их имена — из тех же списков,
/// что правила «через VPN» в <c>config/rules.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Проба не всегда видит такой отказ сама. Gemini собирает страницу скриптами:
/// она приходит целиком, а отказ («Something went wrong (1)») приходит уже на
/// запрос к модели, которого проба не шлёт. Обсуждение у Flowseal №17531 (01.11):
/// проверка писала «доступен», а Gemini не работал. ChatGPT, Claude и Grok
/// называют страну на первой же странице — их ловит и замер.
/// </para>
/// <para>
/// Поэтому вердикт здесь — по списку, а не по замеру, и только когда имя шло
/// мимо туннеля и не прибито в hosts: пин на посредника отвечает из-за границы.
/// </para>
/// </remarks>
public static class CountryClosed
{
    /// <summary>Списки раздела «Нейросети, закрытые по стране» в <c>config/rules.yaml</c>.</summary>
    /// <remarks>Сторожит тест: каждый — правило <c>mode: proxy</c> там.</remarks>
    public static readonly string[] Lists =
    [
        "config/lists/chatgpt.txt",
        "config/lists/claude.txt",
        "config/lists/claude-content.txt",
        "config/lists/gemini.txt",
        "config/lists/grok.txt",
    ];

    private static readonly object Gate = new();
    private static HashSet<string>? _zones;
    private static DateTime _read;

    /// <summary>Входит ли имя в эти списки — само или зоной.</summary>
    public static bool Covers(string host) => Covers(host, Zones());

    internal static bool Covers(string host, IReadOnlySet<string> zones)
    {
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();

        while (name.Length > 0)
        {
            if (zones.Contains(name))
                return true;

            var dot = name.IndexOf('.');
            if (dot < 0)
                return false;

            name = name[(dot + 1)..];
        }

        return false;
    }

    /// <summary>Читает списки заново не чаще раза в минуту — проверка спрашивает по каждому имени.</summary>
    private static HashSet<string> Zones()
    {
        lock (Gate)
        {
            if (_zones is not null && DateTime.UtcNow - _read < TimeSpan.FromMinutes(1))
                return _zones;

            _zones = Read(Lists);
            _read = DateTime.UtcNow;
            return _zones;
        }
    }

    internal static HashSet<string> Read(IEnumerable<string> paths)
    {
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                zones.Add(line.TrimStart('.').TrimEnd('.').ToLowerInvariant());
            }
        }

        return zones;
    }
}
