using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Игровой UDP Riot мимо перехвата десинка (30.09): решает маршрут части
/// «Игровой UDP», по умолчанию — «напрямую».
/// </summary>
public sealed class UdpOffDesyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-udpoff-{Guid.NewGuid():N}");

    public UdpOffDesyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config", "lists"));
        File.WriteAllLines(Path.Combine(_root, "config", "lists", "riot-network.txt"),
            ["# сети Riot", "104.160.128.0/19", "185.40.64.0/22"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private RuleEngine Load(string yaml, string? root = null)
    {
        var engine = RuleSetLoader.Load(yaml);
        RuleSetExpander.Expand(engine.RuleSet, root ?? _root);
        return engine;
    }

    private static string Riot(string mode) => $"""
        mode: selective
        rules:
          - match: ipset
            value: "config/lists/riot-network.txt"
            mode: {mode}
        """;

    [Theory]
    [InlineData("direct")]
    [InlineData("proxy")]
    public void OffDesyncUnlessSetToDesync(string mode)
    {
        Assert.Equal(["104.160.128.0/19", "185.40.64.0/22"], UdpOffDesync.Choose(Load(Riot(mode)), _root));
    }

    [Fact]
    public void DesyncKeepsItInTheCapture()
    {
        Assert.Empty(UdpOffDesync.Choose(Load(Riot("desync")), _root));
    }

    /// <summary>Правила нет — решает «по умолчанию», у выборочного режима это десинк.</summary>
    [Fact]
    public void WithoutARuleTheDefaultDecides()
    {
        Assert.Empty(UdpOffDesync.Choose(Load("mode: selective\nrules: []"), _root));
    }

    /// <summary>
    /// Из коробки — «напрямую»: базовое правило в config/rules.yaml, и «Маршруты»
    /// показывают часть тем же маршрутом, по которому её выводит winws2.
    /// </summary>
    [Fact]
    public void OutOfTheBoxTheGameIsOffDesync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);

        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "NetZapret.sln")))
            repository = repository.Parent;

        if (repository is null)
            return;

        var root = repository.FullName;
        var engine = Load(File.ReadAllText(Path.Combine(root, "config", "rules.yaml")), root);

        Assert.NotEmpty(UdpOffDesync.Choose(engine, root));

        var riot = ServiceCatalog.All.Single(s => s.Name == "Riot и Valorant");
        var parts = ServiceRouting.Describe(riot, engine, root, UserRulesFile.Load(Path.Combine(_root, "rules.user.yaml")));

        Assert.Equal(RoutingMode.Direct, parts.Single(p => p.Part.ByAddress).Mode);
        Assert.Equal(RoutingMode.Desync, parts.Single(p => !p.Part.ByAddress).Mode);
    }

    /// <summary>Пустой список тоже пишется — иначе вчерашний файл выводил бы Riot и после «десинка».</summary>
    [Fact]
    public void WrittenAndReadBackIncludingEmpty()
    {
        var path = Path.Combine(_root, "runtime", "desync-udp-off.txt");

        UdpOffDesync.Write(["104.160.128.0/19"], path);
        Assert.Equal(["104.160.128.0/19"], UdpOffDesync.Read(path));

        UdpOffDesync.Write([], path);
        Assert.True(File.Exists(path));
        Assert.Empty(UdpOffDesync.Read(path));

        Assert.Empty(UdpOffDesync.Read(Path.Combine(_root, "нет-такого.txt")));
    }
}
