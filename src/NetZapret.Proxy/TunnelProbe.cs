using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetZapret.Proxy;

/// <summary>
/// Соединяется с хостом нарочно через туннель, а не как получится.
/// </summary>
/// <remarks>
/// <para>
/// Обычная проба узнаёт путь по адресу: fakeip означает туннель, настоящий
/// адрес — прямой ход. Это отвечает на вопрос «куда ушёл замер», но не на
/// «а что будет, если пустить имя через туннель». Второй вопрос и есть самый
/// нужный там, где имя закрыто напрямую: заводить его в VPN или не поможет.
/// </para>
/// <para>
/// Спрашивается это через служебный вход <c>health-in</c> — инбаунд
/// <c>mixed</c> на петле, который движок поднимает рядом с TUN. Имя уезжает
/// туда именем, а не адресом, поэтому fakeip не нужен вовсе: sing-box
/// сопоставляет доменные правила сам.
/// </para>
/// <para>
/// Через HTTP CONNECT, а не SOCKS: инбаунд <c>mixed</c> обслуживает оба,
/// а CONNECT — это три строки текста, тогда как SOCKS потребовал бы своего
/// разборщика. Тем же путём и по той же причине ходит
/// <see cref="ProxyProbe"/>.
/// </para>
/// </remarks>
public static class TunnelProbe
{
    /// <summary>Сколько ждать ответа от служебного входа.</summary>
    /// <remarks>
    /// Короче сетевых сроков намеренно: вход живёт на петле, и если он есть,
    /// он отвечает мгновенно. Долгое ожидание здесь означало бы, что мы ждём
    /// не соединения, а самого движка.
    /// </remarks>
    private static readonly TimeSpan Reach = TimeSpan.FromSeconds(2);

    /// <summary>Принимает ли служебный вход соединения прямо сейчас.</summary>
    /// <remarks>
    /// Проверяется делом, а не по настройке. Инбаунд поднимается движком,
    /// а настройка лишь просит его поднять: разойтись они могут запросто —
    /// настройку изменили и не перезапустили движки.
    /// </remarks>
    public static async Task<bool> IsUpAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Reach);

            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Открывает поток до <paramref name="host"/> через служебный вход.
    /// </summary>
    /// <remarks>
    /// Возвращённым потоком дальше распоряжается вызывающий: поверх него
    /// встаёт тот же <c>SslStream</c>, что и у прямой пробы, и меряется то же
    /// самое. В этом и смысл — разойдись два замера кодом, сравнивать их
    /// стало бы нельзя.
    /// </remarks>
    /// <exception cref="IOException">Вход отказал или ответил не тем.</exception>
    public static async Task<TcpClient> ConnectAsync(
        int port,
        string host,
        int target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);

            var stream = client.GetStream();

            // Имя, а не адрес: sing-box разрешит его сам на своей стороне,
            // и доменные правила сработают так же, как для настоящего
            // приложения. Разреши мы имя здесь, в туннель уехал бы адрес,
            // и правило по домену мимо него прошло бы.
            var request = Encoding.ASCII.GetBytes(
                $"CONNECT {host}:{target} HTTP/1.1\r\nHost: {host}:{target}\r\n\r\n");

            await stream.WriteAsync(request, limit.Token);

            var line = await ReadStatusLineAsync(stream, limit.Token);

            // 2xx — вход согласился и дальше по этому же сокету идёт голый
            // поток до хоста. Всё прочее означает отказ, и он наш, а не сайта:
            // до сайта мы ещё не дошли.
            if (!line.Contains(" 200", StringComparison.Ordinal))
                throw new IOException($"служебный вход отказал: {line.Trim()}");

            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Читает ответ входа до пустой строки.
    /// </summary>
    /// <remarks>
    /// По байту, и это не расточительство: после заголовков сразу начинается
    /// поток до хоста, и прочитав лишнее, мы съели бы начало чужого
    /// рукопожатия. Заголовков тут три строки, читать их побайтно дешевле,
    /// чем городить возврат прочитанного.
    /// </remarks>
    private static async Task<string> ReadStatusLineAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var one = new byte[1];

        // Потолок на случай, если на том конце не наш вход, а что-то
        // болтливое: без него чужой ответ читался бы до истечения срока.
        const int limit = 4096;

        while (text.Length < limit)
        {
            int read = await stream.ReadAsync(one, cancellationToken);

            if (read == 0)
                throw new IOException("служебный вход закрыл соединение молча");

            text.Append((char)one[0]);

            if (text.Length >= 4
                && text[^4] == '\r' && text[^3] == '\n'
                && text[^2] == '\r' && text[^1] == '\n')
            {
                break;
            }
        }

        var all = text.ToString();
        int end = all.IndexOf("\r\n", StringComparison.Ordinal);

        return end < 0 ? all : all[..end];
    }
}
