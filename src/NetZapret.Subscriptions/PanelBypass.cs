using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using NetZapret.Core;

namespace NetZapret.Subscriptions;

/// <summary>
/// Панель подписки мимо туннеля — когда через туннель она не ответила.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт DxHell 27.10: две подписки из трёх с 26.10 читались только из запаса —
/// «панель не ответила за 12 с». У одной панель на hynet.click, а у человека
/// своё правило <c>*.hynet.click → VPN</c>: запрос уходил в туннель, туннель
/// лежал на сервере из того самого устаревшего запаса, и подписка, которая
/// могла бы его починить, не обновлялась. Порочный круг.
/// </para>
/// <para>
/// Понять, что запрос ушёл в туннель, можно по ответу системы на имя: движок
/// отдаёт именам «через VPN» подставные адреса (198.18.0.0/15, fc00::/18).
/// Тогда — ещё раз: настоящий адрес по DoH, соединение привязано к физическому
/// адаптеру (<see cref="PhysicalNetwork.Interface"/>, IP_UNICAST_IF — тот же
/// способ, что у проб проверки блокировок), имя — в SNI и Host, как было.
/// Сперва туннель, потом напрямую: правило человек ставил не зря — панель
/// может быть закрыта у провайдера.
/// </para>
/// <para>
/// Не проверено вживую: ответит ли панель DxHell напрямую, неизвестно.
/// В 21:06:57 она не ответила и при остановленных движках — но остановка
/// шла 14 с, и туннель мог быть ещё жив.
/// </para>
/// </remarks>
internal static class PanelBypass
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Отдала ли система имени подставной адрес движка — то есть ушёл ли запрос в туннель.</summary>
    public static async Task<bool> TunnelledAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out _))
            return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);

            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            return addresses.Length > 0 && addresses.All(IsFake);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Подставной адрес движка: 198.18.0.0/15 или fc00::/18.</summary>
    internal static bool IsFake(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 198 && (bytes[1] & 0xFE) == 18
            : bytes[0] == 0xFC && bytes[1] == 0x00 && (bytes[2] & 0xC0) == 0;
    }

    /// <summary>
    /// Клиент мимо туннеля; <c>null</c> — физического адаптера нет, идти некуда.
    /// </summary>
    public static HttpClient? Client(TimeSpan timeout)
    {
        if (PhysicalNetwork.Interface() is not { } nic)
            return null;

        return new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var address = await ResolveAsync(context.DnsEndPoint.Host, nic, cancellationToken)
                    ?? throw new HttpRequestException($"имя {context.DnsEndPoint.Host} мимо туннеля не разрешилось");

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                try
                {
                    Bind(socket, nic);
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        {
            Timeout = timeout,
        };
    }

    /// <summary>Настоящий адрес IPv4 по DoH Cloudflare, тоже мимо туннеля.</summary>
    private static async Task<IPAddress?> ResolveAsync(string host, int nic, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        using var doh = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                try
                {
                    Bind(socket, nic);
                    await socket.ConnectAsync(IPAddress.Parse("1.1.1.1"), context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        {
            Timeout = ResolveTimeout,
        };

        doh.DefaultRequestHeaders.Accept.Add(new("application/dns-json"));

        try
        {
            using var answer = await doh.GetFromJsonAsync<JsonDocument>(
                $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A", cancellationToken);

            if (answer is null || !answer.RootElement.TryGetProperty("Answer", out var records))
                return null;

            foreach (var record in records.EnumerateArray())
            {
                if (record.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var address)
                    && address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address;
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    /// <summary>IP_UNICAST_IF: исходящие сокета — через этот адаптер; индекс в сетевом порядке байт.</summary>
    private static void Bind(Socket socket, int nic) =>
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(nic));
}
