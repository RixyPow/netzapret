using System.Net;
using System.Text;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Заглушка панели для нелюбимого клиента (подписка Trust, 28.09).
/// </summary>
public sealed class SubscriptionStubTests
{
    private static ProxyServer Server(string host, string security, ushort port = 443) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = host,
        Host = host,
        Port = port,
        Credential = "PLACEHOLDER",
        Transport = "tcp",
        Security = security,
    };

    private static SubscriptionInfo Info(params ProxyServer[] servers) => new() { Servers = servers };

    [Fact]
    public void Many_servers_on_one_address_without_encryption_is_a_stub()
    {
        Assert.True(SubscriptionClient.LooksLikeStub(Info(
            Server("convert-flow.net", "none"), Server("convert-flow.net", "none"), Server("convert-flow.net", ""))));
    }

    [Fact]
    public void Real_subscriptions_are_not()
    {
        // Разные адреса.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("a.example", "none"), Server("b.example", "none"))));

        // Один адрес, но с Reality — так бывает у настоящих: один вход, много выходов.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("gw.example", "reality"), Server("gw.example", "reality"))));

        // Подписка из одного сервера.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("one.example", "none"))));
    }

    /// <summary>
    /// Обсуждение №69: при лимите устройств панель отдаёт записи на 0.0.0.0:1 с текстом
    /// причины в имени — StubNotice собирает этот текст, а UnsupportedCount не считает их
    /// неподдерживаемыми протоколами.
    /// </summary>
    [Fact]
    public void ZeroAddressStubsBecomeAPanelNoticeInsteadOfUnsupported()
    {
        var stubs = Info(
            Server("0.0.0.0", "none", 1) with { Tag = "Вы достигли лимита устройств" },
            Server("0.0.0.0", "none", 1) with { Tag = "У нас есть тарифы" },
            Server("0.0.0.0", "none", 1) with { Tag = "на 5, 10, 50 устройств." });

        Assert.Equal(0, stubs.UnsupportedCount);
        Assert.Equal("Вы достигли лимита устройств · У нас есть тарифы · на 5, 10, 50 устройств.", stubs.StubNotice());

        // Когда рядом есть рабочий сервер, заглушка-разделитель предупреждением не становится.
        var mixed = Info(
            Server("0.0.0.0", "none", 1) with { Tag = "── Европа ──" },
            Server("de.example", "tls", 443) with { Tag = "Германия" });

        Assert.Equal(0, mixed.UnsupportedCount);
        Assert.Null(mixed.StubNotice());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Agents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// Обсуждение №80: панель на User-Agent sing-box отдала 200 OK с HTML-страницей
    /// (0 серверов), а под Happ отдаёт настоящую подписку — клиент должен переспросить.
    /// </summary>
    [Fact]
    public async Task Unparseable_200_under_default_agent_retries_as_Happ_and_remembers_it()
    {
        var memory = Path.Combine(Path.GetTempPath(), $"nz-agent-{Guid.NewGuid():N}.json");
        var url = new Uri("https://panel.example.test/sub/token");

        try
        {
            var handler = new StubHandler(req =>
            {
                var ua = req.Headers.UserAgent.ToString();
                var body = ua.StartsWith("Happ/", StringComparison.OrdinalIgnoreCase)
                    ? "vless://11111111-1111-1111-1111-111111111111@de.example:443?security=tls&type=tcp#DE"
                    : "<html><body>Open in Happ</body></html>";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain"),
                };
            });

            using var http = new HttpClient(handler);
            using var client = new SubscriptionClient(http, memoryPath: memory);

            var info = await client.FetchAsync(url, CancellationToken.None);

            Assert.Single(info.Servers);
            Assert.Equal("DE", info.Servers[0].Tag);
            Assert.Equal(2, handler.Agents.Count);
            Assert.Equal(SubscriptionClient.FallbackUserAgent, AgentMemory.Get(url, memory));
        }
        finally
        {
            if (File.Exists(memory))
                File.Delete(memory);
        }
    }

    [Fact]
    public async Task Unparseable_200_under_all_agents_keeps_first_error_without_remembering_Happ()
    {
        var memory = Path.Combine(Path.GetTempPath(), $"nz-agent-{Guid.NewGuid():N}.json");
        var url = new Uri("https://panel.example.test/sub/broken");

        try
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>broken</html>", Encoding.UTF8, "text/html"),
            });

            using var http = new HttpClient(handler);
            using var client = new SubscriptionClient(http, memoryPath: memory);

            var info = await client.FetchAsync(url, CancellationToken.None);

            Assert.Empty(info.Servers);
            Assert.NotEmpty(info.Errors);
            Assert.Equal(2, handler.Agents.Count);
            Assert.Null(AgentMemory.Get(url, memory));
        }
        finally
        {
            if (File.Exists(memory))
                File.Delete(memory);
        }
    }
}
