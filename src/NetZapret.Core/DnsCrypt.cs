using System.Globalization;
using System.Text;

namespace NetZapret.Core;

/// <summary>Чем dnscrypt-proxy спрашивает имена.</summary>
public enum DnsCryptMode
{
    /// <summary>DNSCrypt напрямую к серверу.</summary>
    DnsCrypt,

    /// <summary>DNSCrypt через релей: сервер не видит адреса спросившего.</summary>
    Anonymized,

    /// <summary>Oblivious DoH: запрос зашифрован для сервера и идёт через релей.</summary>
    Odoh,
}

/// <summary>
/// Шифрованный DNS туннеля через dnscrypt-proxy — раздел «Шифрованные» вкладки DNS.
/// </summary>
/// <remarks>
/// <para>
/// По образцу Zapret GUI 21.1.7.228 (04.11): DNSCrypt, анонимный DNSCrypt и ODoH.
/// sing-box ни того, ни другого не умеет, поэтому рядом поднимается dnscrypt-proxy
/// (Frank Denis, ISC) на петле, и резолвер движка спрашивает его обычным UDP —
/// как DNS роутера, только адрес 127.0.0.1.
/// </para>
/// <para>
/// Замер 04.11 у владельца, dnscrypt-proxy 2.1.18 из Zapret GUI при поднятых движках,
/// мимо туннеля: DNSCrypt — 174 живых сервера, rutracker.org, www.youtube.com,
/// chatgpt.com и instagram.com — честные адреса за 51–323 мс; анонимный — 184 пути
/// через релеи, 166–298 мс, одно имя из трёх не ответило за 5 с; ODoH — через два
/// релея, 128–154 мс. Списки серверов — снимок DNSCrypt/dnscrypt-resolvers v3 с
/// подписью, едут с программой: из России их адрес на GitHub отвечает не всегда,
/// а без списка dnscrypt-proxy не поднимется. Свежее он скачает сам, когда сможет.
/// </para>
/// </remarks>
public static class DnsCrypt
{
    /// <summary>Порт на петле. Не 53: его держит служба DNS Windows, не 5353 — mDNS.</summary>
    public const int Port = 53530;

    public const string Address = "127.0.0.1";

    /// <summary>
    /// Строка вывода после замера серверов — с ней прокси отвечает: «Server with
    /// the lowest initial latency: …, live servers: N» (2.1.20; «is ready» он не пишет).
    /// </summary>
    public const string ReadyMarker = "live servers:";

    /// <summary>Подпись списков серверов DNSCrypt/dnscrypt-resolvers.</summary>
    public const string ListKey = "RWQf6LRCGA9i53mlYecO4IzT51TGPpvWucNSCh1CBM0QTaLn73Y7GFO3";

    public const string ProcessName = "dnscrypt-proxy";

    public static IReadOnlyList<DnsCryptMode> Modes { get; } = [DnsCryptMode.DnsCrypt, DnsCryptMode.Anonymized, DnsCryptMode.Odoh];

    /// <summary>Где движок лежит в поставке.</summary>
    public static string Executable(string? baseDirectory = null) =>
        Path.Combine(ShippedDirectory(baseDirectory), ProcessName + ".exe");

    /// <summary>Папка движка: там же списки серверов, которыми он стартует.</summary>
    public static string ShippedDirectory(string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "engines", "dnscrypt");

    /// <summary>Рабочая папка: конфиг и списки, которые dnscrypt-proxy обновляет сам.</summary>
    public static string RuntimeDirectory => Path.Combine("runtime", "dnscrypt");

    /// <summary>Ключ режима в настройках (<see cref="AppSettings.DnsCrypt"/>).</summary>
    public static string Key(DnsCryptMode mode) => mode switch
    {
        DnsCryptMode.Anonymized => "anonymized",
        DnsCryptMode.Odoh => "odoh",
        _ => "dnscrypt",
    };

    public static DnsCryptMode? Parse(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "dnscrypt" => DnsCryptMode.DnsCrypt,
        "anonymized" => DnsCryptMode.Anonymized,
        "odoh" => DnsCryptMode.Odoh,
        _ => null,
    };

    public static (string Title, string Note) Describe(DnsCryptMode mode) => mode switch
    {
        DnsCryptMode.Anonymized => ("DNSCrypt анонимный", "шифрует запросы, через релей — сервер не видит ваш адрес"),
        DnsCryptMode.Odoh => ("ODoH", "через HTTPS и релей, пробный"),
        _ => ("DNSCrypt", "шифрует запросы, самый быстрый из живых серверов"),
    };

    /// <summary>Списки серверов, нужные режиму: файл и его подпись рядом.</summary>
    public static IReadOnlyList<string> Lists(DnsCryptMode mode) => mode switch
    {
        DnsCryptMode.Anonymized => ["public-resolvers.md", "relays.md"],
        DnsCryptMode.Odoh => ["odoh-servers.md", "odoh-relays.md"],
        _ => ["public-resolvers.md"],
    };

    /// <summary>
    /// Конфиг dnscrypt-proxy для режима; пути — полные, в <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// Серверы — только без журнала и без фильтрации (<c>require_nolog</c>,
    /// <c>require_nofilter</c>): выбор «шифрованный» — про то, чтобы ответ
    /// не подменили, а не про чужую фильтрацию. IPv6 выключен: у владельца
    /// его нет, и dnscrypt-proxy тратил бы первые проверки на недоступные адреса.
    /// Бутстрап — Quad9 и Google по UDP: он нужен лишь для имён в адресах
    /// списков, сами серверы в списках заданы адресами.
    /// </remarks>
    public static string Config(DnsCryptMode mode, string directory)
    {
        var text = new StringBuilder();

        void Line(string line) => text.Append(line).Append('\n');
        string Quote(string value) => "'" + value.Replace("\\", "/", StringComparison.Ordinal) + "'";

        Line($"listen_addresses = ['{Address}:{Port.ToString(CultureInfo.InvariantCulture)}']");
        Line("ipv6_servers = false");
        Line($"dnscrypt_servers = {(mode == DnsCryptMode.Odoh ? "false" : "true")}");
        Line("doh_servers = false");
        Line($"odoh_servers = {(mode == DnsCryptMode.Odoh ? "true" : "false")}");
        Line("require_nolog = true");
        Line("require_nofilter = true");
        Line("require_dnssec = false");
        Line("bootstrap_resolvers = ['9.9.9.11:53', '8.8.8.8:53']");
        Line("netprobe_timeout = 5");
        Line("cache = true");
        Line("log_level = 2");

        Line(string.Empty);
        Line("[sources]");

        foreach (var list in Lists(mode))
        {
            var name = Path.GetFileNameWithoutExtension(list);

            Line($"  [sources.'{name}']");
            Line($"  urls = ['https://raw.githubusercontent.com/DNSCrypt/dnscrypt-resolvers/master/v3/{list}', 'https://download.dnscrypt.info/resolvers-list/v3/{list}']");
            Line($"  cache_file = {Quote(Path.GetFullPath(Path.Combine(directory, list)))}");
            Line($"  minisign_key = '{ListKey}'");
            Line("  refresh_delay = 72");
        }

        if (mode != DnsCryptMode.DnsCrypt)
        {
            Line(string.Empty);
            Line("[anonymized_dns]");
            Line("routes = [{ server_name = '*', via = ['*'] }]");

            if (mode == DnsCryptMode.Anonymized)
                Line("skip_incompatible = true");
        }

        return text.ToString();
    }

    /// <summary>
    /// Кладёт в рабочую папку списки из поставки, которых там ещё нет.
    /// </summary>
    /// <remarks>
    /// Уже лежащие не трогаются: dnscrypt-proxy обновляет их сам, и свежий
    /// список из сети лучше снимка из архива.
    /// </remarks>
    public static void SeedLists(DnsCryptMode mode, string shippedDirectory, string directory)
    {
        Directory.CreateDirectory(directory);

        foreach (var list in Lists(mode))
        {
            foreach (var file in new[] { list, list + ".minisig" })
            {
                var target = Path.Combine(directory, file);
                var source = Path.Combine(shippedDirectory, file);

                if (!File.Exists(target) && File.Exists(source))
                    File.Copy(source, target);
            }
        }
    }
}
