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

        using var client = new SubscriptionClient(new HttpClient(handler));
        await client.FetchAsync(new Uri("https://panel.example/sub/abc"), CancellationToken.None);

        var request = handler.Request!;

        Assert.Equal(DeviceIdentity.Id, Assert.Single(request.Headers.GetValues("x-hwid")));
        Assert.Equal("Windows", Assert.Single(request.Headers.GetValues("x-device-os")));
        Assert.True(request.Headers.Contains("x-ver-os"));
        Assert.True(request.Headers.Contains("x-device-model"));

        // Представляемся по-прежнему движком: формат ответа панели зависит от него.
        Assert.StartsWith("sing-box/", request.Headers.UserAgent.ToString());
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
