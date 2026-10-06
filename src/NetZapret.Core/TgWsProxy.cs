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
    public static IReadOnlyList<string> Arguments(int port) =>
    [
        "--host", "127.0.0.1",
        "--link-ip", "127.0.0.1",
        "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--default-domains",
    ];
}
