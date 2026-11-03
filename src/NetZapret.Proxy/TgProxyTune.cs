using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Рабочий домен Cloudflare для прокси Telegram и его время ответа.</summary>
public sealed record TgBridge(string Domain, int Ms);

/// <summary>Итог автонастройки прокси Telegram.</summary>
/// <param name="Direct">Прямой путь к DC2 и DC4 (149.154.167.220) отвечает переходом на WebSocket.</param>
/// <param name="Bridges">Домены Cloudflare, ответившие переходом, — быстрые первыми.</param>
/// <param name="Tried">Сколько доменов проверено.</param>
public sealed record TgProxyTuning(DateTimeOffset At, bool Direct, int? DirectMs, IReadOnlyList<TgBridge> Bridges, int Tried);

/// <summary>
/// Автонастройка прокси Telegram: какие пути к Telegram сейчас живы.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 03.11: «давай сделаем» — по образцу Zapret-Zen (<c>tg_proxy_tuner.py</c>).
/// Прокси (<c>--default-domains</c>) сам скачивает общий список доменов Flowseal
/// и перебирает его по порядку. Замер 03.11 у владельца: из 20 доменов 16
/// отвечают на переход к <c>kws2.&lt;домен&gt;/apiws</c> кодом 503 (рейтлимит
/// Cloudflare, Flowseal на ntc.party 22.10), первые тринадцать в списке —
/// мёртвые; переход прошёл у havegreatday, pomogite, fixtelega и onedaychamp
/// за 130–176 мс. Прямой путь к 149.154.167.220 (kws2 и kws4.web.telegram.org) —
/// 101 за 410–473 мс, а выключен он был по умолчанию: у кого адрес закрыт,
/// первое соединение ждало бы таймаут.
/// </para>
/// <para>
/// Поэтому проверяем тем же, что делает прокси, — переходом на WebSocket, а не
/// одним TCP, как Zapret-Zen: соединение встаёт и у домена, отвечающего 503.
/// Прямой путь включается, только если ответил; рабочие домены идут первыми
/// ключом <c>--cf-domain</c>, а общий список остаётся за ними запасом — прокси
/// ставит <c>--cf-domain</c> перед списком <c>--default-domains</c> (проверено
/// на 2.6.0: в шапке запуска сперва свои, затем 20 общих).
/// </para>
/// <para>
/// Проба идёт обычным сокетом и системным DNS — как ходит сам прокси, и через
/// десинк, если он поднят. Перед каждым запуском прокси и кнопкой на вкладке,
/// а не раз в сутки: живые домены меняются за минуты. 03.11 у владельца в 16:00
/// живы были 4 из 20 (havegreatday, pomogite, fixtelega, onedaychamp), в 16:20 —
/// 8 из 20, и первым встал pclead, двадцатью минутами раньше отвечавший 503.
/// Проверка — 20 рукопожатий с Cloudflare и одно с Telegram, 2,6 с.
/// Не ответило ничего — берётся прошлый итог: это скорее сеть, чем Telegram.
/// </para>
/// </remarks>
public static class TgProxyTune
{
    /// <summary>Сколько рабочих доменов ставить первыми.</summary>
    public const int Keep = 5;

    /// <summary>Где лежит итог.</summary>
    public static string StatePath => Path.Combine("runtime", "tg-proxy-tune.json");

    /// <summary>Прямой адрес DC2 и DC4 — тот же, что <see cref="TgWsProxy.DirectMain"/>.</summary>
    private const string DirectAddress = "149.154.167.220";

    /// <summary>Список Flowseal и его зеркало (jsDelivr) — тем, у кого закрыт raw.githubusercontent.com.</summary>
    private static readonly string[] ListUrls =
    [
        "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt",
        "https://cdn.jsdelivr.net/gh/Flowseal/tg-ws-proxy@main/.github/cfproxy-domains.txt",
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Домен общего списка в настоящий вид. Flowseal пишет их сдвигом букв:
    /// у <c>имя.com</c> каждая буква сдвинута на число букв имени, а настоящая
    /// зона — <c>.co.uk</c>. Так их читает и сам прокси — в шапке они <c>.co.uk</c>.
    /// </summary>
    public static string Decode(string line)
    {
        var name = line.Trim().ToLowerInvariant();

        if (!name.EndsWith(".com", StringComparison.Ordinal))
            return name;

        var prefix = name[..^4];
        int letters = prefix.Count(char.IsAsciiLetter);

        var plain = new string(prefix.Select(c => char.IsAsciiLetterLower(c)
            ? (char)('a' + (((c - 'a' - letters) % 26) + 26) % 26)
            : c).ToArray());

        return plain + ".co.uk";
    }

    /// <summary>Итог прошлой проверки; <c>null</c> — её не было или файл испорчен.</summary>
    public static TgProxyTuning? Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<TgProxyTuning>(File.ReadAllText(path ?? StatePath), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(TgProxyTuning tuning, string? path = null)
    {
        var target = path ?? StatePath;

        if (Path.GetDirectoryName(target) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        File.WriteAllText(target, JsonSerializer.Serialize(tuning, Json));
    }

    /// <summary>Пути прокси с учётом автонастройки: она включена — решает замер сейчас.</summary>
    /// <remarks>
    /// Не ответило ничего — прошлый итог; его нет и проверка не удалась — пути
    /// из настроек, как без автонастройки.
    /// </remarks>
    public static async Task<TgWsProxyRoutes> RoutesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var manual = TgWsProxyRoutes.From(settings);

        if (!settings.TelegramProxyAuto)
            return manual;

        var tuning = Load();

        try
        {
            var fresh = await RunAsync(cancellationToken);

            // Ни одного ответа — скорее сеть, чем Telegram: прежний итог честнее.
            if (fresh.Direct || fresh.Bridges.Count > 0 || tuning is null)
            {
                Save(fresh);
                tuning = fresh;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Не вышло — остаётся прежний итог или настройки.
        }

        return Apply(manual, tuning);
    }

    /// <summary>Пути из настроек, поправленные итогом проверки.</summary>
    public static TgWsProxyRoutes Apply(TgWsProxyRoutes manual, TgProxyTuning? tuning) =>
        tuning is null
            ? manual
            : manual with
            {
                DirectMain = tuning.Direct,
                CfDomains = tuning.Bridges.Take(Keep).Select(b => b.Domain).ToList(),
            };

    /// <summary>Итог словами — для вкладки и журнала.</summary>
    public static string Describe(TgProxyTuning? tuning)
    {
        if (tuning is null)
            return "Ещё не проверялось.";

        var direct = tuning.Direct ? $"отвечает, {tuning.DirectMs} мс" : "не отвечает";
        var bridges = tuning.Bridges.Count == 0
            ? $"живых доменов Cloudflare нет из {tuning.Tried}"
            : $"живых доменов Cloudflare {tuning.Bridges.Count} из {tuning.Tried}, первый — {tuning.Bridges[0].Domain} ({tuning.Bridges[0].Ms} мс)";

        return $"Проверено {tuning.At.ToLocalTime():dd.MM HH:mm}: прямой путь к DC2 и DC4 {direct}; {bridges}.";
    }

    /// <summary>Проверяет прямой путь и общий список доменов.</summary>
    public static async Task<TgProxyTuning> RunAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var domains = await DomainsAsync(cancellationToken);

            var direct = ProbeDirectAsync(cancellationToken);
            using var slots = new SemaphoreSlim(6);

            var probes = await Task.WhenAll(domains.Select(async domain =>
            {
                await slots.WaitAsync(cancellationToken);

                try
                {
                    // Сначала kws2, затем kws4 — главные DC у Telegram Desktop.
                    var ms = await UpgradeAsync(null, $"kws2.{domain}", cancellationToken)
                             ?? await UpgradeAsync(null, $"kws4.{domain}", cancellationToken);

                    return ms is { } value ? new TgBridge(domain, value) : null;
                }
                finally
                {
                    slots.Release();
                }
            }));

            var directMs = await direct;

            return new TgProxyTuning(
                DateTimeOffset.Now,
                directMs is not null,
                directMs,
                probes.OfType<TgBridge>().OrderBy(b => b.Ms).ToList(),
                domains.Count);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Общий список Flowseal; не скачался — домены прошлой проверки.</summary>
    private static async Task<IReadOnlyList<string>> DomainsAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetZapret");

        foreach (var url in ListUrls)
        {
            try
            {
                var text = await http.GetStringAsync(url, cancellationToken);
                var list = Parse(text);

                if (list.Count >= 3)
                    return list;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Следующее зеркало.
            }
        }

        return Load()?.Bridges.Select(b => b.Domain).ToList() ?? [];
    }

    /// <summary>Строки списка — в домены, без пустых, заметок и повторов.</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(Decode)
            .Where(d => d.Contains('.'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Прямой путь: переход на WebSocket к kws2, затем kws4.web.telegram.org на адрес DC.</summary>
    private static async Task<int?> ProbeDirectAsync(CancellationToken cancellationToken) =>
        await UpgradeAsync(DirectAddress, "kws2.web.telegram.org", cancellationToken)
        ?? await UpgradeAsync(DirectAddress, "kws4.web.telegram.org", cancellationToken);

    /// <summary>
    /// Переход на WebSocket к <c>/apiws</c>, как у прокси; время до ответа 101 или <c>null</c>.
    /// </summary>
    /// <param name="address"><c>null</c> — адрес по имени через системный DNS.</param>
    internal static async Task<int?> UpgradeAsync(string? address, string host, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeTimeout);

        var clock = Stopwatch.StartNew();

        try
        {
            var target = address is not null
                ? IPAddress.Parse(address)
                : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, limit.Token)).FirstOrDefault();

            if (target is null)
                return null;

            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(target, 443, limit.Token);

            // Сертификат не сверяется — так и у прокси к доменам Cloudflare
            // (у них свой); судим только по ответу 101.
            using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, limit.Token);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = Encoding.ASCII.GetBytes(
                $"GET /apiws HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n");

            await ssl.WriteAsync(request, limit.Token);

            var buffer = new byte[512];
            int read = await ssl.ReadAsync(buffer, limit.Token);
            var head = Encoding.ASCII.GetString(buffer, 0, read);

            return head.StartsWith("HTTP/1.1 101", StringComparison.Ordinal)
                ? (int)clock.ElapsedMilliseconds
                : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
