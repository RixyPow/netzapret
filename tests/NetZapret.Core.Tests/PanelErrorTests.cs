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

    [Fact]
    public void ANetworkFailureIsNotAnEnglishMessage()
    {
        var error = new HttpRequestException("No connection could be made", new SocketException(10061));

        Assert.Equal("панель недоступна — соединение не установилось", PanelError.Describe(error));
    }
}
