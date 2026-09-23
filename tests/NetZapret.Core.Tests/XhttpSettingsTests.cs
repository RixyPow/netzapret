using System.Diagnostics;
using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Настройки XHTTP из подписки доходят до движка.
/// </summary>
/// <remarks>
/// 23.09 подписка из девяти серверов XHTTP целиком молчала через движок:
/// в <c>extra</c> поставщик требовал набивку «1», а мы писали свою
/// «100-1000», и сервер отвергал каждый запрос. Весь трафик VPN уходил
/// в WARP — единственный живой выход, — и с ним ложились Telegram и Instagram.
/// </remarks>
public sealed class XhttpSettingsTests
{
    // Форма та же, что у поставщика; адрес, ключ и путь выдуманы.
    private const string Link =
        "vless://00000000-0000-0000-0000-000000000000@xh.example.com:443" +
        "?encryption=none&type=xhttp&path=%2Fp&host=xh.example.com&mode=auto" +
        "&extra=%7B%22xmux%22%3A%7B%22maxConnections%22%3A%222-4%22%2C%22hKeepAlivePeriod%22%3A5%7D" +
        "%2C%22noSSEHeader%22%3Atrue%2C%22noGRPCHeader%22%3Atrue%2C%22xPaddingBytes%22%3A%221%22%7D" +
        "&security=tls&sni=xh.example.com&fp=firefox&alpn=h2%2Chttp%2F1.1#XH";

    [Fact]
    public void LinkExtraReachesTheTransport()
    {
        var transport = Transport(Parse(Link));

        Assert.Equal("auto", transport.GetProperty("mode").GetString());
        Assert.Equal("1", transport.GetProperty("x_padding_bytes").GetString());
        Assert.True(transport.GetProperty("no_sse_header").GetBoolean());
        Assert.True(transport.GetProperty("no_grpc_header").GetBoolean());

        var xmux = transport.GetProperty("xmux");
        Assert.Equal("2-4", xmux.GetProperty("max_connections").GetString());
        Assert.Equal(5, xmux.GetProperty("h_keep_alive_period").GetInt64());
    }

    /// <summary>
    /// Поставщик своей набивки не назвал — остаётся наша по умолчанию:
    /// без неё движок не читает конфиг.
    /// </summary>
    [Theory]
    [InlineData("""{"noSSEHeader":true}""")]
    [InlineData("""{"xPaddingBytes":0}""")]
    [InlineData("""{"xPaddingBytes":"0"}""")]
    public void PaddingFallsBackWhenNotGiven(string extra)
    {
        var server = Parse(Link) with { XhttpOptions = XhttpSettings.FromXrayText(extra) };

        Assert.Equal("100-1000", Transport(server).GetProperty("x_padding_bytes").GetString());
    }

    /// <summary>
    /// Незнакомое поле sing-box не пропускает, а отвергает конфиг целиком —
    /// с туннелем вместе. Такое отсеивается ещё при разборе.
    /// </summary>
    [Fact]
    public void UnknownFieldsAndHostHeaderAreDropped()
    {
        var options = XhttpSettings.FromXrayText("""
            {"xPaddingBytes":"1","downloadSettings":{"address":"x"},"someFutureKnob":7,
             "headers":{"Host":"evil.example.com","X-Test":"1"}}
            """);

        using var parsed = JsonDocument.Parse(options!);
        var names = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(["x_padding_bytes", "headers"], names);
        Assert.False(parsed.RootElement.GetProperty("headers").TryGetProperty("Host", out _));
    }

    /// <summary>Битый extra не стоит сервера.</summary>
    [Fact]
    public void BrokenExtraKeepsTheServer()
    {
        Assert.Null(XhttpSettings.FromXrayText("{not json"));
    }

    [Fact]
    public void UnknownModeIsLeftToTheEngineDefault()
    {
        Assert.Null(XhttpSettings.Mode("gun"));
        Assert.Equal("packet-up", XhttpSettings.Mode("packet-up"));
    }

    /// <summary>
    /// В конфиге Xray поля лежат и прямо в xhttpSettings, и в extra;
    /// extra важнее. Диапазон объектом приводится к строке.
    /// </summary>
    [Fact]
    public void XraySectionMergesExtraOverTopLevel()
    {
        using var section = JsonDocument.Parse("""
            {"mode":"auto","path":"/p","xPaddingBytes":{"from":5,"to":9},"noSSEHeader":false,
             "extra":{"noSSEHeader":true,"scMaxEachPostBytes":1000000}}
            """);

        using var options = JsonDocument.Parse(XhttpSettings.FromXraySection(section.RootElement)!);
        var root = options.RootElement;

        Assert.Equal("5-9", root.GetProperty("x_padding_bytes").GetString());
        Assert.True(root.GetProperty("no_sse_header").GetBoolean());
        Assert.Equal(1000000, root.GetProperty("sc_max_each_post_bytes").GetInt64());
        Assert.False(root.TryGetProperty("path", out _));
    }

    /// <summary>Настоящий движок принимает то, что получилось.</summary>
    [Fact]
    public void ConfigWithProviderSettingsPassesSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Parse(Link)], new SingBoxOptions());

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-xhttp-check-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, result.Json);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"sing-box check не пройден: {stderr}");
        }
        finally
        {
            File.Delete(path);
            File.Delete(SingBoxConfigCompiler.StampPathFor(path));
        }
    }

    private static ProxyServer Parse(string link)
    {
        Assert.True(ProxyUriParser.TryParse(link, out var server, out var error), error);
        return server!;
    }

    private static JsonElement Transport(ProxyServer server)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [server], new SingBoxOptions()).Json;

        return JsonDocument.Parse(json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == server.Tag)
            .GetProperty("transport").Clone();
    }

    private static string? FindSingBox()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools)
                && Directory.EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
