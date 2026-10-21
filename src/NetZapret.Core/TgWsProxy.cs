using System.Security.Cryptography;

namespace NetZapret.Core;

/// <summary>
/// Прокси для Telegram Desktop без VPN — tg-ws-proxy-rs третьим движком.
/// </summary>
/// <remarks>
/// <para>
/// Порт на Rust (valnesfjord/tg-ws-proxy-rs, MIT) программы Flowseal
/// tg-ws-proxy (MIT): локальный прокси MTProto, который ведёт трафик Telegram
/// через WebSocket по TLS — к веб-адресам самого Telegram или через домены
/// за Cloudflare. Владелец 07.10: «я хочу готовый прокси как экзешник» —
/// и из двух готовых выбран порт: он консольный и встаёт под надзор, как
/// winws2 и sing-box, а у оригинала под Windows есть только программа
/// с треем и окнами.
/// </para>
/// <para>
/// Замер 07.10 у владельца, «Десинк», туннеля нет (sing-box не запущен,
/// из адаптеров один Wi-Fi), Telegram Desktop на 127.0.0.1:1443. Без
/// <c>--default-domains</c> — переписка шла (DC2 и DC4 через WebSocket),
/// медиа нет: DC203 не знает адреса WebSocket и падал на прямом TCP,
/// 29 таймаутов из 35. С ним — «всё идеально грузит»: DC203 через
/// Cloudflare 14 раз из 14. Список доменов берётся с GitHub, а не ответил —
/// зашитый в сам прокси.
/// </para>
/// <para>
/// Только на этой машине: <c>--host 127.0.0.1</c> — без него прокси слушает
/// все адреса, если видит локальную сеть, — и <c>--link-ip 127.0.0.1</c>:
/// без него в ссылку попадал адрес нашего TUN (172.19.0.1, тот же замер).
/// </para>
/// </remarks>
public static class TgWsProxy
{
    public const int DefaultPort = 1443;

    /// <summary>Переменная, из которой прокси берёт секрет, — чтобы его не было в командной строке.</summary>
    public const string SecretVariable = "TG_SECRET";

    /// <summary>Строка вывода, после которой прокси принимает соединения.</summary>
    public const string ListeningMarker = "Listening on";

    /// <summary>Где движок лежит в поставке.</summary>
    public static string Executable(string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "engines", "tg-ws-proxy", "tg-ws-proxy.exe");

    /// <summary>Новый секрет — 16 случайных байтов, 32 шестнадцатеричных знака.</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Годится ли строка в секрет прокси.</summary>
    public static bool IsSecret(string? secret) =>
        secret is { Length: 32 } && secret.All(Uri.IsHexDigit);

    /// <summary>
    /// Ссылка для Telegram Desktop: щелчок по ней добавляет прокси.
    /// </summary>
    /// <remarks>
    /// Секрет с приставкой <c>dd</c> — режим «padded», в котором прокси
    /// принимает соединения (его вывод: «Inbound mode: padded MTProto dd»).
    /// </remarks>
    public static string Link(int port, string secret) =>
        $"tg://proxy?server=127.0.0.1&port={port}&secret=dd{secret}";

    /// <summary>
    /// Настройки с включённым или выключенным прокси.
    /// </summary>
    /// <remarks>
    /// Секрет создаётся при первом включении и дальше не меняется: Telegram
    /// помнит прокси с ним, и новый секрет на каждое включение заставлял бы
    /// подключать его заново. Одно место для вкладки «TG Proxy» и мастера.
    /// </remarks>
    public static AppSettings Switch(AppSettings settings, bool on) => settings with
    {
        TelegramProxy = on,
        TelegramProxySecret = on && !IsSecret(settings.TelegramProxySecret)
            ? NewSecret()
            : settings.TelegramProxySecret,
    };

    /// <summary>
    /// Нужен ли прокси при таком режиме — пояснение для выбора в мастере.
    /// </summary>
    /// <remarks>
    /// Без VPN адреса Telegram закрыты (замер 07.10, «Десинк»: без прокси
    /// медиа не шло ни разу), а в режимах с туннелем Telegram идёт через VPN
    /// по адресам списка, и прокси там запасной путь.
    /// </remarks>
    public static string Explain(Rules.WorkMode mode) => mode == Rules.WorkMode.Desync
        ? "В «Десинке» VPN нет, а адреса Telegram закрыты: без прокси Telegram Desktop "
          + "не подключится. С ним работает целиком, с фото и видео. Только Telegram Desktop "
          + "на этом компьютере — телефон через него не подключить. Подключить Telegram к нему — "
          + "кнопкой на вкладке «TG Proxy»."
        : "В «Гибриде» и «Туннеле» Telegram идёт через VPN — прокси не обязателен, но и не мешает: "
          + "Telegram Desktop, подключённый к нему, ходит мимо VPN. Только Telegram Desktop "
          + "на этом компьютере. Подключить Telegram к нему — кнопкой на вкладке «TG Proxy».";

    /// <summary>Ключи запуска; секрет — не здесь, а в <see cref="SecretVariable"/>.</summary>
    /// <remarks>
    /// <para>
    /// <c>--default-domains</c> — ради медиа: без него DC203 и прочие дата-центры
    /// без прямого адреса шли на голый TCP и падали (замер 07.10, 29 таймаутов
    /// из 35). Но этот ключ молча снимает и прямые адреса по умолчанию (DC2 и DC4
    /// → 149.154.167.220): в шапке запуска «Target DC IPs:» пусто у 2.5.2, 2.5.4
    /// и 2.5.6, и весь Telegram шёл через домены Cloudflare общего списка. 21.10
    /// у владельца из них жили два, и те через раз: за 3,5 ч 321 соединение,
    /// 292 ответа 503 и 20 таймаутов на пути, 80 сеансов Telegram закрыл сам без
    /// единого байта — «работает нестабильно». Поэтому прямые адреса задаются
    /// явно (<see cref="TgWsProxyRoutes"/>), а Cloudflare остаётся запасным путём, —
    /// выключателями на вкладке «TG Proxy», по умолчанию выключенными (владелец 21.10).
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Arguments(int port, TgWsProxyRoutes? routes = null)
    {
        routes ??= new TgWsProxyRoutes();

        var arguments = new List<string>
        {
            "--host", "127.0.0.1",
            "--link-ip", "127.0.0.1",
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--default-domains",
        };

        if (routes.DirectMain)
        {
            foreach (var target in DirectMain)
                arguments.AddRange(["--dc-ip", target]);
        }

        if (routes.DirectOther)
        {
            foreach (var target in DirectOther)
                arguments.AddRange(["--dc-ip", target]);
        }

        int timeout = CfTimeout(routes.CfConnectTimeout);

        if (timeout != DefaultCfConnectTimeout)
        {
            arguments.AddRange(["--cf-connect-timeout",
                timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    /// <summary>
    /// Прямые адреса DC2 и DC4 — те, что прокси берёт по умолчанию без
    /// <c>--default-domains</c>.
    /// </summary>
    /// <remarks>
    /// Самопроверка прокси 21.10 у владельца (<c>--check-listener</c>, настоящий
    /// <c>req_pq_multi</c> к DC2): только Cloudflare — 185, 372 и 406 мс, с этими
    /// адресами — 101–112 мс, «DC2 → WS connected via 149.154.167.220».
    /// </remarks>
    public static IReadOnlyList<string> DirectMain { get; } = ["2:149.154.167.220", "4:149.154.167.220"];

    /// <summary>
    /// Прямые адреса DC1, DC3 и DC5 — веб-входы Telegram по честному DoH
    /// (kws1 и kws3.web.telegram.org — 149.154.174.100, kws5 — 149.154.170.100).
    /// </summary>
    /// <remarks>
    /// 21.10 в сети владельца все три отвечали по TLS за 0,56–0,68 с. Самопроверка
    /// прокси ходит только в DC2, и рабочий ли это путь для настоящих соединений,
    /// не проверено — поэтому выключено по умолчанию.
    /// </remarks>
    public static IReadOnlyList<string> DirectOther { get; } =
        ["1:149.154.174.100", "3:149.154.174.100", "5:149.154.170.100"];

    /// <summary>Ожидание домена Cloudflare по умолчанию у самого прокси, секунды.</summary>
    public const int DefaultCfConnectTimeout = 10;

    /// <summary>Ожидание домена Cloudflare в разумных пределах.</summary>
    public static int CfTimeout(int seconds) => seconds is >= 1 and <= 30 ? seconds : DefaultCfConnectTimeout;
}

/// <summary>Пути прокси к Telegram — настройки вкладки «TG Proxy» (владелец 21.10).</summary>
/// <param name="DirectMain">Прямой путь к DC2 и DC4 (<see cref="TgWsProxy.DirectMain"/>).</param>
/// <param name="DirectOther">Прямой путь к DC1, DC3 и DC5 (<see cref="TgWsProxy.DirectOther"/>).</param>
/// <param name="CfConnectTimeout">Сколько ждать один домен Cloudflare, секунды.</param>
public sealed record TgWsProxyRoutes(
    bool DirectMain = false,
    bool DirectOther = false,
    int CfConnectTimeout = TgWsProxy.DefaultCfConnectTimeout)
{
    public static TgWsProxyRoutes From(AppSettings settings) => new(
        settings.TelegramProxyDirect,
        settings.TelegramProxyDirectOther,
        settings.TelegramProxyCfTimeout);
}
