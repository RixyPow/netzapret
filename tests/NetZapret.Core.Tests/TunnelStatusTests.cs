using System.Net;
using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Что программа узнаёт у движка про его собственный туннель.
/// </summary>
/// <remarks>
/// Проверяется поддельным Clash API на петле. Живой движок здесь не нужен
/// и вреден: ответы его зависят от того, какая подписка сегодня жива,
/// а ломается тут разбор — в том, что считается ответом, а что незнанием.
/// </remarks>
public sealed class TunnelStatusTests
{
    /// <summary>Clash API, отвечающий заготовленным по пути запроса.</summary>
    private sealed class FakeApi : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public int Port { get; }

        public FakeApi(Func<string, (int Code, string Body)> answer)
        {
            Port = FreePort();

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
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

                    var (code, body) = answer(context.Request.Url?.PathAndQuery ?? string.Empty);
                    var bytes = Encoding.UTF8.GetBytes(body);

                    context.Response.StatusCode = code;
                    context.Response.ContentLength64 = bytes.Length;

                    try
                    {
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        // Слушатель закрыт раньше ответа — конец теста.
                    }
                }
            });
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            _stop.Dispose();
        }
    }

    /// <summary>Обычный случай: селектор указывает прямо на сервер.</summary>
    [Fact]
    public async Task The_chosen_server_is_named()
    {
        await using var api = new FakeApi(_ =>
            (200, """{"type":"Selector","now":"🇩🇪 Германия"}"""));

        var server = await TunnelStatus.CurrentServerAsync(CancellationToken.None, api.Port);

        Assert.Equal("🇩🇪 Германия", server);
    }

    /// <summary>
    /// Селектор указывает на группу — спрашиваем ещё раз у неё.
    /// </summary>
    /// <remarks>
    /// Иначе ответом на «через что мы сейчас ходим» было бы «auto-latency»,
    /// то есть имя способа выбора, а не сервера.
    /// </remarks>
    [Fact]
    public async Task A_group_is_asked_again_for_its_own_choice()
    {
        await using var api = new FakeApi(path => path.Contains("auto-latency")
            ? (200, """{"type":"URLTest","now":"Финляндия"}""")
            : (200, """{"type":"Selector","now":"auto-latency"}"""));

        Assert.Equal(
            "Финляндия",
            await TunnelStatus.CurrentServerAsync(CancellationToken.None, api.Port));
    }

    /// <summary>
    /// Группа, ещё не выбравшая, называется сама — а не пустотой.
    /// </summary>
    /// <remarks>
    /// Так отвечает URLTest до первого замера задержки: поле <c>now</c> есть,
    /// но пустое. Замер 17.09: движки подняты в 17:43:28, отчёт снят через
    /// полминуты — и в шапке стояло «Сервер: » с пустотой после двоеточия,
    /// а в 17:46 та же группа уже называла «Германия — TLS XHTTP».
    /// </remarks>
    [Fact]
    public async Task A_group_that_has_not_chosen_yet_names_itself()
    {
        await using var api = new FakeApi(path => path.Contains("auto-latency")
            ? (200, """{"type":"URLTest","now":""}""")
            : (200, """{"type":"Selector","now":"auto-latency"}"""));

        Assert.Equal(
            "auto-latency",
            await TunnelStatus.CurrentServerAsync(CancellationToken.None, api.Port));
    }

    /// <summary>Пустое имя у самого селектора — это незнание.</summary>
    /// <remarks>
    /// Показывается оно как «не определён», и для этого должно быть именно
    /// отсутствием, а не пустой строкой: пустая проходила мимо проверки
    /// и оставляла в отчёте строку, обрывающуюся на двоеточии.
    /// </remarks>
    [Fact]
    public async Task An_empty_name_is_not_a_name()
    {
        await using var api = new FakeApi(_ => (200, """{"type":"Selector","now":"   "}"""));

        Assert.Null(await TunnelStatus.CurrentServerAsync(CancellationToken.None, api.Port));
    }

    /// <summary>Молчащий движок — тоже незнание, а не выдуманное имя.</summary>
    [Fact]
    public async Task A_silent_engine_gives_no_name()
    {
        await using var api = new FakeApi(_ => (500, "нет"));

        Assert.Null(await TunnelStatus.CurrentServerAsync(CancellationToken.None, api.Port));
    }

    /// <summary>Ответ на запрос задержки решает, жив ли туннель.</summary>
    [Theory]
    [InlineData(200, TunnelState.Alive)]
    [InlineData(503, TunnelState.Dead)]
    public async Task The_delay_answer_decides(int code, TunnelState expected)
    {
        await using var api = new FakeApi(_ => (code, """{"delay":120}"""));

        Assert.Equal(
            expected,
            await TunnelStatus.StateAsync("Финляндия", CancellationToken.None, api.Port));
    }

    /// <summary>
    /// Без имени сервера спрашивается селектор, а не поднимаются руки.
    /// </summary>
    /// <remarks>
    /// Запрос задержки к группе заставляет её выбрать, так что ответ есть
    /// и тогда, когда выбора ещё не было. Прежде здесь возвращалось
    /// «состояние не выяснено», и отчёт, снятый сразу после запуска движков,
    /// молчал о туннеле при живом туннеле.
    /// </remarks>
    [Fact]
    public async Task Without_a_server_name_the_selector_is_asked()
    {
        string? asked = null;

        await using var api = new FakeApi(path =>
        {
            asked = path;
            return (200, """{"delay":120}""");
        });

        Assert.Equal(
            TunnelState.Alive,
            await TunnelStatus.StateAsync(null, CancellationToken.None, api.Port));

        Assert.NotNull(asked);
        Assert.Contains("/proxies/auto/delay", asked);
    }

    /// <summary>
    /// Недоступная диагностика — незнание, а не приговор.
    /// </summary>
    /// <remarks>
    /// Объявить туннель мёртвым по недоступности его же диагностики значило бы
    /// обвинить исправную трубу.
    /// </remarks>
    [Fact]
    public async Task An_unreachable_api_is_not_a_verdict()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Assert.Equal(
            TunnelState.Unknown,
            await TunnelStatus.StateAsync("Финляндия", CancellationToken.None, free));
    }
}
