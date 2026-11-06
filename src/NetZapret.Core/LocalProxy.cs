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
    /// <param name="ownNow">Порт заведомо наш — проверять, свободен ли он, не нужно (тесты).</param>
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

        // Порт держит наш же движок — не помеха. По настройкам этого не узнать:
        // 06.11 прокси был поднят, человек спрятал карточку (настройка
        // выключилась, движок не перезапускался) и включил снова — и окно
        // назвало свой же порт «занят другой программой».
        if (!ownNow && !HeldByEngine(port) && !IsFree(port))
            return $"Порт {port} уже занят другой программой — выберите другой.";

        return null;
    }

    /// <summary>
    /// Слушает ли этот порт вход прокси в конфиге, с которым поднят движок.
    /// </summary>
    /// <remarks>
    /// Конфиг пишется перед каждым запуском движков, так что его вход — то,
    /// что сейчас слушает sing-box. Остановленный движок порт отпускает, и
    /// тогда проверка привязкой ниже его увидит свободным.
    /// </remarks>
    public static bool HeldByEngine(int port, string? configPath = null)
    {
        try
        {
            var path = configPath ?? Path.Combine("runtime", "singbox.json");
            if (!File.Exists(path))
                return false;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("inbounds", out var inbounds))
                return false;

            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (inbound.TryGetProperty("tag", out var tag) && tag.GetString() == "local-proxy"
                    && inbound.TryGetProperty("listen_port", out var listen) && listen.TryGetInt32(out int held)
                    && held == port)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
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
