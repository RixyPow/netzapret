using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Отказ панели — словами и с кодом, а не текстом исключения (01.10).</summary>
public sealed class PanelErrorTests
{
    [Fact]
    public void AServerErrorIsTheVendors()
    {
        var error = new HttpRequestException("Response status code does not indicate success: 502 (Bad Gateway).",
            null, HttpStatusCode.BadGateway);

        Assert.Equal("панель ответила ошибкой 502 — неполадка на сервере продавца", PanelError.Describe(error));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "панель отказала (403) — ссылка устарела или подписка закрыта")]
    [InlineData(HttpStatusCode.NotFound, "панель не знает такой ссылки (404) — подписку могли сменить")]
    [InlineData(HttpStatusCode.TooManyRequests, "панель просит обращаться реже (429)")]
    public void CodesAreNamed(HttpStatusCode code, string expected)
    {
        Assert.Equal(expected, PanelError.Status(code));
    }

    [Theory]
    [InlineData(10061, "панель отказала в соединении — сервер жив, но порт закрыт")]
    [InlineData(11001, "имя панели не разрешилось — DNS не знает его или не ответил")]
    [InlineData(10065, "панель недоступна — соединение не установилось")]
    public void ANetworkFailureIsNotAnEnglishMessage(int socketError, string expected)
    {
        var error = new HttpRequestException("No connection could be made", new SocketException(socketError));

        Assert.Equal(expected, PanelError.Describe(error, online: () => true));
    }

    /// <summary>
    /// Без сети панель ни при чём: 23–24.10 у владельца Wi-Fi подключался через
    /// минуты после пробуждения, а под подписками стояло «панель недоступна».
    /// </summary>
    [Fact]
    public void WithoutANetworkThePanelIsNotBlamed()
    {
        var refused = new HttpRequestException("No such host is known", new SocketException(11001));
        var answered = new HttpRequestException("502", null, HttpStatusCode.BadGateway);

        Assert.Equal(PhysicalNetwork.Offline, PanelError.Describe(refused, online: () => false));
        Assert.Equal(PhysicalNetwork.Offline, PanelError.Describe(new TimeoutException(), online: () => false));

        // Ответ с кодом — уже доказательство сети, её и не спрашиваем.
        Assert.Equal("панель ответила ошибкой 502 — неполадка на сервере продавца",
            PanelError.Describe(answered, online: () => throw new InvalidOperationException()));
    }
}
