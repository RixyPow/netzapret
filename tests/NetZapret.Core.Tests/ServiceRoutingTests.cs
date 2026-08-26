using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Вид по сервисам: Discord, YouTube и прочее так, как о них думает человек.
/// </summary>
/// <remarks>
/// Ценность появляется там, где части сервиса расходятся: у Discord голос
/// ломается иначе, чем переписка, и лечится иначе.
/// </remarks>
public sealed class ServiceRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-svc-{Guid.NewGuid():N}");

    public ServiceRoutingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "lists"));

        Write("discord-media.txt", "discord.media");
        Write("discord.txt", "discord.com", "discord.gg", "discordapp.com");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, "lists", name), lines);

    private static ServiceDefinition Discord => new()
    {
        Name = "Discord",
        Parts =
        [
            new ServicePart { Name = "Голос", List = "lists/discord-media.txt" },
            new ServicePart { Name = "Текст", List = "lists/discord.txt" },
        ],
    };

    private RuleEngine Load(string yaml)
    {
        var engine = RuleSetLoader.Load(yaml);
        RuleSetExpander.Expand(engine.RuleSet, _root);
        return engine;
    }

    private static ConnectionEvent Connection(string host) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Protocol = ProtocolKind.Tcp,
        RemoteAddress = IPAddress.Parse("203.0.113.7"),
        RemotePort = 443,
        Hostname = host,
    };

    [Fact]
    public void AHostListRuleCoversEveryDomainInTheFile()
    {
        var engine = Load("""
            mode: selective
            rules:
              - match: hostlist
                value: "lists/discord.txt"
                mode: proxy
            default:
              mode: desync
            """);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("discord.com")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("discord.gg")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("discordapp.com")).Mode);
    }

    [Fact]
    public void EntriesInAListAreZonesNotExactNames()
    {
        // В списках Zapret пишут discord.media, подразумевая и поддомены:
        // голосовые серверы называются russia1234.discord.media. Толковать
        // записи буквально значило бы не поймать ровно то, ради чего список нужен.
        var engine = Load("""
            mode: selective
            rules:
              - match: hostlist
                value: "lists/discord-media.txt"
                mode: proxy
            default:
              mode: desync
            """);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("russia1234.discord.media")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("discord.media")).Mode);
    }

    [Fact]
    public void PartsOfOneServiceCanGoDifferentWays()
    {
        // Ровно то, ради чего затевалось разделение.
        var engine = Load("""
            mode: selective
            rules:
              - match: hostlist
                value: "lists/discord-media.txt"
                mode: proxy
            default:
              mode: desync
            """);

        var parts = ServiceRouting.Describe(Discord, engine, _root, EmptyUserRules());

        Assert.Equal(RoutingMode.Proxy, parts[0].Mode);
        Assert.Equal(RoutingMode.Desync, parts[1].Mode);
        Assert.Contains("голос VPN", ServiceRouting.Summarize(parts));
        Assert.Contains("текст десинк", ServiceRouting.Summarize(parts));
    }

    [Fact]
    public void WhenEverythingAgreesTheSummaryIsShort()
    {
        var engine = Load("""
            mode: selective
            default:
              mode: desync
            """);

        Assert.Equal("всё десинк", ServiceRouting.Summarize(
            ServiceRouting.Describe(Discord, engine, _root, EmptyUserRules())));
    }

    [Fact]
    public void ARuleWrittenByHandIsRecognisedInTheService()
    {
        // Иначе список начнёт врать: человек направил домен вручную,
        // а сервис показывает прежнее.
        var engine = Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.discord.media"
                mode: direct
            default:
              mode: desync
            """);

        var parts = ServiceRouting.Describe(Discord, engine, _root, EmptyUserRules());

        Assert.Equal(RoutingMode.Direct, parts[0].Mode);
    }

    [Fact]
    public void AMissingListIsReportedRatherThanMatchingEverything()
    {
        var service = new ServiceDefinition
        {
            Name = "Нет такого",
            Parts = [new ServicePart { Name = "Всё", List = "lists/нет-файла.txt" }],
        };

        var parts = ServiceRouting.Describe(service, Load("mode: selective"), _root, EmptyUserRules());

        Assert.Equal(0, parts[0].DomainCount);
        Assert.Equal("списки не найдены", ServiceRouting.Summarize(parts));
    }

    [Fact]
    public void AnUnloadedListMatchesNothingAtAll()
    {
        // Правило, чей файл не нашёлся, не должно совпасть со всем подряд:
        // это увело бы в туннель весь трафик вместо одного сервиса.
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: hostlist
                value: "lists/нет-файла.txt"
                mode: proxy
            default:
              mode: desync
            """);

        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection("example.com")).Mode);
    }

    [Fact]
    public void EveryServiceInTheCatalogHasParts()
    {
        Assert.NotEmpty(ServiceCatalog.All);

        Assert.All(ServiceCatalog.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.NotEmpty(s.Parts);

            Assert.All(s.Parts, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
                Assert.StartsWith("lists/", p.List);
            });
        });
    }

    [Fact]
    public void ServiceNamesAreUnique()
    {
        // Иначе Find вернёт первый попавшийся, а человек выберет другой.
        var names = ServiceCatalog.All.Select(s => s.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static UserRulesFile EmptyUserRules() =>
        UserRulesFile.Load(Path.Combine(Path.GetTempPath(), $"netzapret-none-{Guid.NewGuid():N}.yaml"));
}
