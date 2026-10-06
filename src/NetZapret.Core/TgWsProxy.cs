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

    /// <summary>Ключи запуска; секрет — не здесь, а в <see cref="SecretVariable"/>.</summary>
    public static IReadOnlyList<string> Arguments(int port) =>
    [
        "--host", "127.0.0.1",
        "--link-ip", "127.0.0.1",
        "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--default-domains",
    ];
}
