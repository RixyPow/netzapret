using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

public class EngineKeysTests
{
    private static string Compile(EngineKeys? keys)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");

        return new SingBoxConfigCompiler().Compile(engine.RuleSet, [], new SingBoxOptions
        {
            HealthInbound = true,
            Keys = keys,
        }).Json;
    }

    [Fact]
    public void KeysWrittenByCompilerAreReadBack()
    {
        // Клиенты берут пароли из того же файла, который пишет компилятор.
        // Разойдись запись и чтение — окно и сторож получали бы 401 и 407
        // от собственного движка и считали его мёртвым.
        var keys = EngineKeys.Generate();

        Assert.Equal(keys, EngineKeys.Parse(Compile(keys)));
    }

    [Fact]
    public void WithoutKeysInboundsStayOpen()
    {
        // Сборки для проверок и конфиг движка прежней версии паролей не знают:
        // тогда и клиенту предъявлять нечего, и заголовок не шлётся вовсе.
        var json = Compile(null);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.False(root.GetProperty("experimental").GetProperty("clash_api").TryGetProperty("secret", out _));
        Assert.DoesNotContain("\"users\"", json);

        var parsed = EngineKeys.Parse(json);
        Assert.True(parsed is null || (parsed.ClashSecret.Length == 0 && parsed.User.Length == 0));
    }

    [Fact]
    public void GeneratedKeysDifferEachTime()
    {
        Assert.NotEqual(EngineKeys.Generate(), EngineKeys.Generate());
    }

    [Fact]
    public void ProbeInboundGetsItsOwnKeys()
    {
        var keys = EngineKeys.Generate();
        var json = new SingBoxConfigCompiler().CompileProbeConfig(
            new ProxyServer
            {
                Protocol = ProxyProtocol.Trojan,
                Tag = "probe",
                Host = "probe.example.com",
                Port = 443,
                Credential = "PLACEHOLDER",
                Transport = "tcp",
                Security = "tls",
                Sni = "probe.example.com",
            },
            21099, logPath: null, keys: keys);

        var user = JsonDocument.Parse(json).RootElement
            .GetProperty("inbounds")[0].GetProperty("users")[0];

        Assert.Equal(keys.User, user.GetProperty("username").GetString());
        Assert.Equal(keys.Password, user.GetProperty("password").GetString());
    }
}
