using System.Text.Json.Nodes;
using NetZapret.Proxy;
using NetZapret.Core.Rules;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Шифрование VLESS доходит до движка, а строка, которую он не разберёт, — нет.
/// </summary>
/// <remarks>
/// До 29.10 <c>encryption</c> не читался: сервер, который его требует, молча
/// «умирал». Что движок принимает — сверено <c>sing-box check</c> 29.10
/// (см. <see cref="VlessEncryption"/>); любая непонятая строка роняет весь
/// конфиг, поэтому такой сервер пропускается.
/// </remarks>
public sealed class VlessEncryptionTests
{
    // 32 байта base64url — ключ x25519.
    private const string X25519 = "VdK-7rvc7W8OcZVuWIaIXUhtitb472v0AbCdEfGhIjk";

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("none", "")]
    [InlineData(" NONE ", "")]
    [InlineData("mlkem768x25519plus.native.0rtt." + X25519, "mlkem768x25519plus.native.0rtt." + X25519)]
    [InlineData("mlkem768x25519plus.xorpub.1rtt." + X25519, "mlkem768x25519plus.xorpub.1rtt." + X25519)]
    [InlineData("mlkem768x25519plus.random.0rtt." + X25519 + "." + X25519, "mlkem768x25519plus.random.0rtt." + X25519 + "." + X25519)]
    public void What_the_engine_takes_passes(string? value, string expected)
    {
        Assert.Equal(expected, VlessEncryption.ForSingBox(value));
    }

    /// <summary>Каждая из этих строк роняла <c>sing-box check</c> 29.10.</summary>
    [Theory]
    [InlineData("garbage.value")]
    [InlineData("MLKEM768X25519PLUS.native.0rtt." + X25519)]
    [InlineData("mlkem768x25519plus.foo.0rtt." + X25519)]
    [InlineData("mlkem768x25519plus.native.2rtt." + X25519)]
    [InlineData("mlkem768x25519plus.native.0rtt.100-111-1111.75-0-111." + X25519)]
    [InlineData("mlkem768x25519plus.native.0rtt." + X25519 + "=")]
    [InlineData("mlkem768x25519plus.native.0rtt.short")]
    [InlineData("mlkem768x25519plus.native.0rtt")]
    public void What_would_crash_the_engine_is_refused(string value)
    {
        Assert.Null(VlessEncryption.ForSingBox(value));
    }

    [Fact]
    public void A_random_1184_byte_key_is_not_an_ml_kem_key()
    {
        var bytes = new byte[1184];
        Array.Fill(bytes, (byte)0xFF);
        var key = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(VlessEncryption.ForSingBox("mlkem768x25519plus.native.0rtt." + key));

        Array.Clear(bytes);
        key = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.NotNull(VlessEncryption.ForSingBox("mlkem768x25519plus.native.0rtt." + key));
    }

    [Fact]
    public void The_link_carries_it_into_the_config_and_a_bad_one_is_skipped()
    {
        Assert.True(ProxyUriParser.TryParse(
            $"vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@enc.example.com:443?security=tls&sni=enc.example.com&encryption=mlkem768x25519plus.native.0rtt.{X25519}#enc",
            out var good, out _));
        Assert.True(ProxyUriParser.TryParse(
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@bad.example.com:443?security=tls&encryption=mlkem768x25519plus.native.0rtt.100-111-1111.abc#bad",
            out var bad, out _));
        Assert.True(ProxyUriParser.TryParse(
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@plain.example.com:443?security=tls&encryption=none#plain",
            out var plain, out _));

        Assert.True(good!.IsSupportedBySingBox);
        Assert.False(bad!.IsSupportedBySingBox);
        Assert.Equal("шифрование VLESS в виде, которого движок не разбирает", bad.Unusable);
        Assert.Null(plain!.Unusable);

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [good, plain], new SingBoxOptions());

        var outbounds = JsonNode.Parse(result.Json)!["outbounds"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(
            "mlkem768x25519plus.native.0rtt." + X25519,
            outbounds.Single(o => (string?)o["tag"] == "enc")["encryption"]!.GetValue<string>());
        Assert.Null(outbounds.Single(o => (string?)o["tag"] == "plain")["encryption"]);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }
}
