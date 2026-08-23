using System.Text.Json;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"netzapret-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void SettingsSurviveRoundTrip()
    {
        var settings = new AppSettings
        {
            SubscriptionUrl = "https://example.com/sub/token",
            Mode = OperatingMode.ProxyAll,
            PresetName = "Universal V6",
            PreferredServer = "🇷🇺 Hysteria2 | Россия",
            VerifyTraffic = true,
        };

        settings.Save(_path);
        var loaded = AppSettings.Load(_path);

        Assert.Equal(OperatingMode.ProxyAll, loaded.Mode);
        Assert.Equal("Universal V6", loaded.PresetName);
        Assert.Equal("🇷🇺 Hysteria2 | Россия", loaded.PreferredServer);
        Assert.True(loaded.VerifyTraffic);
    }

    [Fact]
    public void ModeIsStoredAsNameNotNumber()
    {
        // Число в файле настроек поехало бы при любой вставке значения
        // в середину перечисления, причём молча.
        new AppSettings { Mode = OperatingMode.ProxyAll }.Save(_path);

        Assert.Contains("ProxyAll", File.ReadAllText(_path));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = AppSettings.Load(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json"));

        Assert.Equal(OperatingMode.Selective, settings.Mode);
        Assert.True(settings.ProxyOnly);
        Assert.Null(settings.PresetName);
    }

    [Fact]
    public void CorruptedFileGivesDefaultsInsteadOfThrowing()
    {
        // Иначе испорченный файл настроек не даст программе запуститься вовсе.
        File.WriteAllText(_path, "{ это не json");

        Assert.Equal(OperatingMode.Selective, AppSettings.Load(_path).Mode);
    }

    [Fact]
    public void NullPresetMeansDesyncIsNotStarted()
    {
        Assert.Equal("не запускать", new AppSettings().DescribePreset());
        Assert.Equal("авто (по задержке)", new AppSettings().DescribeServer());
    }
}

public class PreferredServerTests
{
    private static ProxyServer Server(string tag) => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = tag,
        Host = "example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Security = "tls",
    };

    private static JsonElement Selector(string? preferred)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var json = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server("Россия"), Server("США")], new SingBoxOptions
            {
                PreferredServerTag = preferred,
            }).Json;

        return JsonDocument.Parse(json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "selector")
            .Clone();
    }

    [Fact]
    public void PinnedServerBecomesTheSelectorDefault()
    {
        Assert.Equal("США", Selector("США").GetProperty("default").GetString());
    }

    [Fact]
    public void WithoutPinTheLatencyTestIsUsed()
    {
        Assert.Equal("auto-latency", Selector(null).GetProperty("default").GetString());
    }

    [Fact]
    public void StaleServerNameFallsBackToLatencyTest()
    {
        // Тег мог остаться в настройках от прежней подписки. Ссылка
        // на отсутствующий outbound не дала бы конфигу запуститься вовсе.
        Assert.Equal("auto-latency", Selector("Сервер которого больше нет").GetProperty("default").GetString());
    }
}
