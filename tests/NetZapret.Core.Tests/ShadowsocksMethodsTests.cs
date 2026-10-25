using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Шифр Shadowsocks — именем, которое знает sing-box (обсуждение №38, 25.10).
/// </summary>
public sealed class ShadowsocksMethodsTests
{
    [Theory]
    [InlineData("chacha20-poly1305", "chacha20-ietf-poly1305")]
    [InlineData("xchacha20-poly1305", "xchacha20-ietf-poly1305")]
    [InlineData("plain", "none")]
    [InlineData("AES-256-GCM", "aes-256-gcm")]
    [InlineData(" chacha20-ietf-poly1305 ", "chacha20-ietf-poly1305")]
    [InlineData("2022-blake3-aes-128-gcm", "2022-blake3-aes-128-gcm")]
    [InlineData("rc4-md5", "rc4-md5")]
    [InlineData(null, ShadowsocksMethods.Default)]
    [InlineData("", ShadowsocksMethods.Default)]
    public void Known_names_and_aliases_become_sing_box_names(string? method, string expected) =>
        Assert.Equal(expected, ShadowsocksMethods.ForSingBox(method));

    /// <summary>
    /// Незнакомый шифр — не годится: сервер пропускается, а не роняет туннель.
    /// </summary>
    [Theory]
    [InlineData("chacha20")]
    [InlineData("2022-blake3-chacha8-poly1305")]
    [InlineData("salsa20")]
    public void An_unknown_method_makes_the_server_unusable(string method)
    {
        Assert.Null(ShadowsocksMethods.ForSingBox(method));

        var server = new ProxyServer
        {
            Protocol = ProxyProtocol.Shadowsocks,
            Tag = "x",
            Host = "1.2.3.4",
            Port = 443,
            Credential = "secret",
            Method = method,
        };

        Assert.False(server.IsUsableOutbound);
        Assert.True((server with { Method = "chacha20-poly1305" }).IsUsableOutbound);
    }
}
