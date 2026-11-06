using System.Net;
using System.Net.Sockets;

namespace NetZapret.Core;

/// <summary>
/// Локальный прокси для программ человека: SOCKS5 и HTTP на 127.0.0.1, всё — в VPN.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №42 и идея №18 (<c>docs/ideas.md</c>): «несистемный прокси» —
/// его видят только программы, где он прописан явно (браузер отдельным
/// профилем, лаунчер, curl). Системный прокси Windows не трогается. Режим
/// «только прокси» без TUN решено не делать — прокси живёт рядом с туннелем.
/// </para>
/// <para>
/// Это вход движка туннеля (<c>SingBoxOptions.LocalProxyPort</c>), а не свой
/// процесс: занятый порт роняет sing-box целиком, поэтому порт проверяется,
/// когда человек его выбирает (<see cref="Problem"/>).
/// </para>
/// </remarks>
public static class LocalProxy
{
    /// <summary>Порт по умолчанию — привычный для клиентов sing-box и Xray.</summary>
    public const int DefaultPort = 2080;

    /// <summary>Адрес для настроек программы.</summary>
    public static string Address(int port) => $"127.0.0.1:{port}";

    /// <summary>
    /// Чем выбранный порт не годится; <c>null</c> — годится.
    /// </summary>
    /// <param name="port">Порт, который выбирает человек.</param>
    /// <param name="settings">Нынешние настройки — чтобы не спорить с портом прокси Telegram.</param>
    /// <param name="ownNow">Порт уже занят нашим же прокси (движок поднят с ним) — не помеха.</param>
    public static string? Problem(int port, AppSettings settings, bool ownNow = false)
    {
        if (port is < 1024 or > 65535)
            return "Порт — число от 1024 до 65535.";

        // Свои порты программы: замер серверов раздаёт 21080 и соседние,
        // вход проверки — 21090, Clash API — 9090, пробный winws2 — 39100–39199.
        if (port is >= 21080 and <= 21199 or 9090 or >= ProbePorts.First and <= ProbePorts.Last)
            return "Этот порт занят самой программой — выберите другой.";

        if (port == settings.TelegramProxyPort)
            return "На этом порту прокси Telegram — выберите другой.";

        if (!ownNow && !IsFree(port))
            return $"Порт {port} уже занят другой программой — выберите другой.";

        return null;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
