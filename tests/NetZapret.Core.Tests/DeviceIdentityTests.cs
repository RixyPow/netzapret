using System.Net;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подписка с привязкой к устройству (HWID): программа называет себя панели.
/// </summary>
/// <remarks>
/// Запрос из чата 23.09 — подписка с HWID не читалась: без заголовка
/// <c>x-hwid</c> панель отдаёт пустоту или отказ.
/// </remarks>
public sealed class DeviceIdentityTests
{
    /// <summary>
    /// Номер постоянный — иначе каждый запуск был бы новым устройством
    /// и упирался бы в лимит панели, — и не сам идентификатор Windows.
    /// </summary>
    [Fact]
    public void IdIsStableHexAndNotTheSeedItself()
    {
        const string seed = "4c4c4544-0047-3010-8047-b4c04f4e5a31";

        var id = DeviceIdentity.FromSeed(seed);

        Assert.Equal(id, DeviceIdentity.FromSeed(seed));
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.DoesNotContain(seed.Replace("-", string.Empty), id);
        Assert.NotEqual(id, DeviceIdentity.FromSeed(seed + "x"));

        Assert.Equal(DeviceIdentity.Id, DeviceIdentity.Id);
    }

    /// <summary>Запрос подписки несёт номер устройства и описание системы.</summary>
    [Fact]
    public async Task SubscriptionRequestCarriesHwidHeaders()
    {
        var handler = new Capture();

        using var client = new SubscriptionClient(new HttpClient(handler), memoryPath: Memory());
        await client.FetchAsync(new Uri("https://panel.example/sub/abc"), CancellationToken.None);

        var request = handler.Request!;

        Assert.Equal(DeviceIdentity.Id, Assert.Single(request.Headers.GetValues("x-hwid")));
        Assert.Equal("Windows", Assert.Single(request.Headers.GetValues("x-device-os")));
        Assert.True(request.Headers.Contains("x-ver-os"));
        Assert.True(request.Headers.Contains("x-device-model"));

        // Представляемся по-прежнему движком: формат ответа панели зависит от него.
        Assert.StartsWith("sing-box/", request.Headers.UserAgent.ToString());
    }

    /// <summary>
    /// Панель, что пускает только Happ с номером устройства, — как у
    /// пользователя 24.09: sing-box получает 404, и программа спрашивает
    /// ещё раз под именем Happ.
    /// </summary>
    [Fact]
    public async Task APanelThatOnlyTalksToHappStillGivesTheSubscription()
    {
        var handler = new HappOnly();

        using var client = new SubscriptionClient(new HttpClient(handler), memoryPath: Memory());
        var info = await client.FetchAsync(new Uri("https://panel.example/cart/abc"), CancellationToken.None);

        Assert.Equal(["sing-box/1.14.0", "Happ/3.4.0"], handler.Agents);
        Assert.Single(info.Servers);
    }

    /// <summary>Сбой самой панели другим именем не лечится — второй раз не спрашиваем.</summary>
    [Fact]
    public async Task APanelFailureIsNotRetriedUnderAnotherName()
    {
        var handler = new HappOnly { Status = HttpStatusCode.BadGateway };

        using var client = new SubscriptionClient(new HttpClient(handler), memoryPath: Memory());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchAsync(new Uri("https://panel.example/cart/abc"), CancellationToken.None));

        Assert.Single(handler.Agents);
    }

    private sealed class HappOnly : HttpMessageHandler
    {
        public List<string> Agents { get; } = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.NotFound;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var agent = request.Headers.UserAgent.ToString();
            Agents.Add(agent);

            bool happ = agent.StartsWith("Happ/", StringComparison.Ordinal)
                && request.Headers.Contains("x-hwid");

            return Task.FromResult(happ
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=tcp#Test"),
                }
                : new HttpResponseMessage(Status) { Content = new StringContent("<html>404</html>") });
        }
    }

    private static string Memory() => Path.Combine(Path.GetTempPath(), $"netzapret-agents-{Guid.NewGuid():N}.json");

    /// <summary>
    /// Заработало под именем Happ — запомнено, и следующее чтение идёт
    /// сразу под ним, без заведомого 404 (владелец, 24.09).
    /// </summary>
    [Fact]
    public async Task TheWorkingNameIsRemembered()
    {
        var memory = Memory();
        var url = new Uri("https://panel.example/cart/abc");

        try
        {
            var first = new HappOnly();
            using (var client = new SubscriptionClient(new HttpClient(first), memoryPath: memory))
                await client.FetchAsync(url, CancellationToken.None);

            Assert.Equal("Happ/3.4.0", AgentMemory.Get(url, memory));

            var second = new HappOnly();
            using (var client = new SubscriptionClient(new HttpClient(second), memoryPath: memory))
                await client.FetchAsync(url, CancellationToken.None);

            Assert.Equal(["Happ/3.4.0"], second.Agents);

            // Файл памяти не хранит саму ссылку — она равносильна паролю.
            Assert.DoesNotContain("cart/abc", File.ReadAllText(memory));
        }
        finally
        {
            File.Delete(memory);
        }
    }

    /// <summary>
    /// Запомненный Happ перестал отвечать, а имени по умолчанию панель снова
    /// отвечает — подписка читается, и память забывается.
    /// </summary>
    [Fact]
    public async Task TheMemoryIsForgottenWhenTheDefaultWorksAgain()
    {
        var memory = Memory();
        var url = new Uri("https://panel.example/sub/xyz");

        try
        {
            AgentMemory.Set(url, "Happ/3.4.0", memory);

            var handler = new NotHapp();
            using (var client = new SubscriptionClient(new HttpClient(handler), memoryPath: memory))
                await client.FetchAsync(url, CancellationToken.None);

            Assert.Equal(["Happ/3.4.0", "sing-box/1.14.0"], handler.Agents);
            Assert.Null(AgentMemory.Get(url, memory));
        }
        finally
        {
            File.Delete(memory);
        }
    }

    private sealed class NotHapp : HttpMessageHandler
    {
        public List<string> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var agent = request.Headers.UserAgent.ToString();
            Agents.Add(agent);

            return Task.FromResult(agent.StartsWith("Happ/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=tcp#Test"),
                });
        }
    }

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) });
        }
    }
}
