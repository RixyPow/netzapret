using System.Net;
using System.Net.Sockets;
using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Разговор со служебным входом: то, что проба говорит движку, и то,
/// как она понимает ответ.
/// </summary>
/// <remarks>
/// Проверяется поддельным входом на петле, а не живым sing-box: движок
/// поднимать ради разбора трёх строк текста незачем, а поломка тут будет
/// именно в тексте — в том, что мы отправили и что приняли за согласие.
/// </remarks>
public sealed class TunnelProbeTests
{
    /// <summary>
    /// Имя уезжает движку именем, а не адресом.
    /// </summary>
    /// <remarks>
    /// В этом весь смысл замера через вход: sing-box сопоставляет доменные
    /// правила по имени, и разреши мы его у себя, в туннель уехал бы адрес —
    /// правило по домену прошло бы мимо, и замер описывал бы не тот путь,
    /// которым ходит настоящее приложение.
    /// </remarks>
    [Fact]
    public async Task The_name_travels_as_a_name()
    {
        string? seen = null;

        await using var proxy = await FakeInbound.StartAsync(request =>
        {
            seen = request;
            return "HTTP/1.1 200 Connection established\r\n\r\n";
        });

        using var client = await TunnelProbe.ConnectAsync(
            proxy.Port, "discord.media", 443, TimeSpan.FromSeconds(4), CancellationToken.None);

        Assert.NotNull(seen);
        Assert.StartsWith("CONNECT discord.media:443 HTTP/1.1", seen);
        Assert.Contains("Host: discord.media:443", seen);
    }

    /// <summary>Согласие входа — это 200, и только оно.</summary>
    [Theory]
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\n")]
    [InlineData("HTTP/1.1 502 Bad Gateway\r\n\r\n")]
    [InlineData("HTTP/1.1 400 Bad Request\r\n\r\n")]
    public async Task A_refusal_is_not_mistaken_for_a_tunnel(string answer)
    {
        await using var proxy = await FakeInbound.StartAsync(_ => answer);

        var failure = await Assert.ThrowsAsync<IOException>(() => TunnelProbe.ConnectAsync(
            proxy.Port, "example.com", 443, TimeSpan.FromSeconds(4), CancellationToken.None));

        Assert.Contains("служебный вход отказал", failure.Message);
    }

    /// <summary>
    /// После заголовков читать нельзя ничего лишнего.
    /// </summary>
    /// <remarks>
    /// Сразу за пустой строкой начинается поток до хоста. Прочитав хоть байт
    /// сверх заголовков, мы съели бы начало чужого рукопожатия — и ошибка
    /// вышла бы не там, где причина.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_read_past_the_headers()
    {
        const string payload = "это уже поток до хоста";

        await using var proxy = await FakeInbound.StartAsync(
            _ => "HTTP/1.1 200 Connection established\r\nProxy-Agent: sing-box\r\n\r\n" + payload);

        using var client = await TunnelProbe.ConnectAsync(
            proxy.Port, "example.com", 443, TimeSpan.FromSeconds(4), CancellationToken.None);

        var buffer = new byte[256];
        int read = await client.GetStream().ReadAsync(buffer);

        Assert.Equal(payload, Encoding.UTF8.GetString(buffer, 0, read));
    }

    /// <summary>Молчание входа — это отказ, а не согласие.</summary>
    [Fact]
    public async Task A_silent_close_is_a_failure()
    {
        await using var proxy = await FakeInbound.StartAsync(_ => null);

        await Assert.ThrowsAsync<IOException>(() => TunnelProbe.ConnectAsync(
            proxy.Port, "example.com", 443, TimeSpan.FromSeconds(4), CancellationToken.None));
    }

    /// <summary>Наличие входа проверяется делом, а не настройкой.</summary>
    [Fact]
    public async Task A_missing_inbound_is_seen()
    {
        int free;

        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            free = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        Assert.False(await TunnelProbe.IsUpAsync(free, CancellationToken.None));
    }

    [Fact]
    public async Task A_live_inbound_is_seen()
    {
        await using var proxy = await FakeInbound.StartAsync(_ => "HTTP/1.1 200 OK\r\n\r\n");

        Assert.True(await TunnelProbe.IsUpAsync(proxy.Port, CancellationToken.None));
    }

    /// <summary>Вход, отвечающий одной заготовленной строкой.</summary>
    private sealed class FakeInbound : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public int Port { get; }

        private FakeInbound(TcpListener listener, Func<string, string?> answer)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;

            _serving = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var request = new StringBuilder();
                            var one = new byte[1];

                            while (request.Length < 4096)
                            {
                                int read = await stream.ReadAsync(one, _stop.Token);

                                if (read == 0)
                                    return;

                                request.Append((char)one[0]);

                                if (request.Length >= 4 && request.ToString().EndsWith("\r\n\r\n"))
                                    break;
                            }

                            if (answer(request.ToString()) is not { } reply)
                                return;

                            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), _stop.Token);

                            // Держим соединение, пока его не закроет проверка:
                            // закрыв сами, мы отняли бы у неё поток, который
                            // она как раз и собирается читать.
                            await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                        }
                    }, _stop.Token);
                }
            }, _stop.Token);
        }

        public static Task<FakeInbound> StartAsync(Func<string, string?> answer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            return Task.FromResult(new FakeInbound(listener, answer));
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();

            try
            {
                await _serving;
            }
            catch (Exception)
            {
                // Отмена — штатный конец.
            }

            _stop.Dispose();
        }
    }
}
