using System.Net;
using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Замер скорости (30.09) — на поддельном сервере замера на петле.
/// </summary>
/// <remarks>
/// Настоящий сервер тут не годится: его цифры — про сеть в день прогона.
/// Проверяется счёт: что этапы идут по порядку, что отказ сервера и замерший
/// поток названы словами, а не выданы за ноль, и что отмена обрывает замер.
/// </remarks>
public sealed class SpeedTestTests
{
    /// <summary>Сервер замера: trace, отдача байт по запросу и приём тела.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();

        public Uri Address { get; }

        /// <summary>Код ответа на скачивание — 200 или отказ.</summary>
        public int DownloadCode { get; init; } = 200;

        /// <summary>Сколько байт отдать и замолчать; <c>null</c> — сколько просят.</summary>
        public int? FreezeAfter { get; init; }

        public long Uploaded;

        public FakeServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Address = new Uri($"http://127.0.0.1:{port}/");

            _listener.Prefixes.Add(Address.ToString());
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext context;

                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _ = Task.Run(() => AnswerAsync(context));
                }
            });
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url!.AbsolutePath;

                if (path == "/cdn-cgi/trace")
                {
                    var trace = Encoding.ASCII.GetBytes("fl=1\nip=203.0.113.7\nloc=EE\ncolo=TLL\n");
                    await context.Response.OutputStream.WriteAsync(trace);
                }
                else if (path == "/__down")
                {
                    int wanted = int.Parse(context.Request.QueryString["bytes"]!);

                    context.Response.StatusCode = DownloadCode;
                    context.Response.Headers["Server-Timing"] = "cfSpeedEdge;dur=1, cfSpeedWorker;dur=2";

                    if (DownloadCode == 200)
                    {
                        context.Response.ContentLength64 = wanted;

                        var chunk = new byte[64 * 1024];
                        int sent = 0;

                        while (sent < wanted)
                        {
                            if (FreezeAfter is { } limit && sent >= limit)
                            {
                                // Замерший поток: соединение открыто, данных нет.
                                await Task.Delay(TimeSpan.FromSeconds(30));
                                break;
                            }

                            int count = Math.Min(chunk.Length, wanted - sent);

                            await context.Response.OutputStream.WriteAsync(chunk.AsMemory(0, count));
                            await context.Response.OutputStream.FlushAsync();
                            sent += count;

                            // Петля быстрее любой сети — придерживаем, чтобы
                            // тест не гнал гигабайты.
                            await Task.Delay(2);
                        }
                    }
                }
                else if (path == "/__up")
                {
                    var buffer = new byte[64 * 1024];
                    int read;

                    while ((read = await context.Request.InputStream.ReadAsync(buffer)) > 0)
                        Interlocked.Add(ref Uploaded, read);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
            catch (Exception)
            {
                // Клиент оборвал соединение — для замера это обычный конец этапа.
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    private static SpeedTest Against(FakeServer server) => new(new SpeedTestOptions
    {
        Server = server.Address,
        Streams = 2,
        Pings = 3,
        DownloadFor = TimeSpan.FromSeconds(1.2),
        UploadFor = TimeSpan.FromSeconds(1.2),
    });

    /// <summary>Показания, собранные без очереди диспетчера: порядок важен.</summary>
    private sealed class Collected : IProgress<SpeedReading>
    {
        public List<SpeedReading> Readings { get; } = [];

        public void Report(SpeedReading value)
        {
            lock (Readings)
                Readings.Add(value);
        }
    }

    [Fact]
    public async Task EverythingIsMeasuredInOrder()
    {
        await using var server = new FakeServer();
        var progress = new Collected();

        var result = await Against(server).RunAsync(progress, CancellationToken.None);

        Assert.Null(result.Problem);
        Assert.True(result.DownMbps > 0, "скачивание не измерено");
        Assert.True(result.UpMbps > 0, "отдача не измерена");
        Assert.NotNull(result.PingMs);
        Assert.NotNull(result.JitterMs);

        Assert.Equal("203.0.113.7", result.Address);
        Assert.Equal("EE", result.Country);
        Assert.Equal("TLL", result.Node);

        Assert.True(result.DownBytes > 0);
        Assert.True(server.Uploaded > 0);

        Assert.True(result.UpBytes > 0);

        // Итог этапа приходит и показанием — по нему окно ставит крупные цифры,
        // не дожидаясь конца всего замера.
        Assert.Equal(result.DownMbps, progress.Readings.Single(r => r is { Phase: SpeedPhase.Download, Done: true }).Mbps);
        Assert.Equal(result.UpMbps, progress.Readings.Single(r => r is { Phase: SpeedPhase.Upload, Done: true }).Mbps);
        Assert.Equal(result.PingMs, progress.Readings.Single(r => r is { Phase: SpeedPhase.Ping, Done: true }).PingMs);

        var phases = progress.Readings.Select(r => r.Phase).ToList();

        Assert.Equal(
            [SpeedPhase.Connect, SpeedPhase.Ping, SpeedPhase.Download, SpeedPhase.Upload],
            phases.Distinct().ToList());

        Assert.Equal(phases.OrderBy(p => p).ToList(), phases);
        Assert.Contains(progress.Readings, r => r.Phase == SpeedPhase.Download && r.Mbps > 0);
        Assert.All(progress.Readings, r => Assert.InRange(r.Fraction, 0, 1));
    }

    /// <summary>Отказ сервера — причина словами, а не «0 Мбит/с».</summary>
    [Fact]
    public async Task ARefusalIsNamed()
    {
        await using var server = new FakeServer { DownloadCode = 403 };

        var result = await Against(server).RunAsync(null, CancellationToken.None);

        Assert.Null(result.DownMbps);
        Assert.Contains("403", result.Problem);

        // Задержку мерит тот же запрос, и он отказан — но до скачивания дело дошло.
        Assert.Equal("EE", result.Country);
    }

    /// <summary>
    /// Поток, замерший на первых килобайтах, — так выглядит замедление
    /// у оператора (Cloudflare без десинка, 28.09) — назван замершим.
    /// </summary>
    [Fact]
    public async Task AFrozenStreamIsNamed()
    {
        await using var server = new FakeServer { FreezeAfter = 16 * 1024 };

        var result = await Against(server).RunAsync(null, CancellationToken.None);

        Assert.Null(result.DownMbps);
        Assert.Contains("замерло", result.Problem);
    }

    [Fact]
    public async Task AnUnreachableServerIsNamed()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var result = await new SpeedTest(new SpeedTestOptions { Server = new Uri($"http://127.0.0.1:{port}/") })
            .RunAsync(null, CancellationToken.None);

        Assert.StartsWith("сервер замера недоступен", result.Problem);
        Assert.Null(result.DownMbps);
        Assert.Null(result.UpMbps);
        Assert.Null(result.PingMs);
    }

    [Fact]
    public async Task CancellingStopsTheMeasurement()
    {
        await using var server = new FakeServer();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        var test = new SpeedTest(new SpeedTestOptions
        {
            Server = server.Address,
            Streams = 2,
            Pings = 2,
            DownloadFor = TimeSpan.FromSeconds(20),
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.RunAsync(null, cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"отмена заняла {watch.Elapsed.TotalSeconds:0.0} с");
    }

    /// <summary>Цифры замера 30.09: из заголовка берутся оба времени сервера, а cfL4 — нет.</summary>
    [Fact]
    public void TheServersOwnTimeIsReadFromItsHeader()
    {
        Assert.Equal(46, SpeedTest.ServerTime(["cfSpeedEdge;dur=6, cfSpeedWorker;dur=40"]));
        Assert.Equal(7.5, SpeedTest.ServerTime(["cfSpeedEdge;dur=7.5", "cfL4;desc=\"?proto=TCP&rtt=21386\""]));
        Assert.Equal(0, SpeedTest.ServerTime(["cfL4;desc=\"?proto=TCP&rtt=21386;dur=9\""]));
        Assert.Equal(0, SpeedTest.ServerTime([]));
    }

    [Fact]
    public void TheTraceIsParsed()
    {
        Assert.Equal(
            ("203.0.113.7", "EE", "TLL"),
            SpeedTest.ParseTrace("fl=1\r\nh=speed.cloudflare.com\r\nip=203.0.113.7\r\nloc=EE\r\ncolo=TLL\r\n"));

        Assert.Equal((null, null, null), SpeedTest.ParseTrace("<html>не то</html>"));
    }

    [Fact]
    public void MedianAndJitter()
    {
        Assert.Equal(20, SpeedTest.Median([30, 10, 20]));
        Assert.Equal(15, SpeedTest.Median([10, 20]));
        Assert.Null(SpeedTest.Median([]));

        Assert.Equal(10, SpeedTest.Jitter([10, 20, 10, 20]));
        Assert.Null(SpeedTest.Jitter([10]));
    }

    [Fact]
    public void TheSummaryNamesWhatWasNotMeasured()
    {
        Assert.Equal("ничего не измерено", SpeedTest.Describe(new SpeedResult()));

        var text = SpeedTest.Describe(new SpeedResult { DownMbps = 120, PingMs = 39, Problem = "отдача не пошла: отказ" });

        Assert.Contains("скачивание 120", text);
        Assert.Contains("задержка 39 мс", text);
        Assert.Contains("отдача не пошла", text);
    }
}
