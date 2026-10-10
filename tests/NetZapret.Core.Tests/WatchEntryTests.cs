using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Журнал наблюдения: строка туда и обратно и сводка по программам для отчёта.
/// </summary>
public sealed class WatchEntryTests
{
    private static ConnectionEvent Connection(string exe, string? host, string address, ushort port, ProtocolKind protocol = ProtocolKind.Tcp) => new()
    {
        Timestamp = new DateTimeOffset(2026, 10, 10, 12, 30, 1, 123, DateTimeOffset.Now.Offset),
        Protocol = protocol,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = port,
        ExecutablePath = $@"C:\Program Files\{exe}",
        Hostname = host,
    };

    /// <summary>Строка журнала разбирается обратно без потерь — на ней стоит сводка отчёта.</summary>
    [Fact]
    public void ALineReadsBackAsTheSameEntry()
    {
        var entry = WatchEntry.From(
            Connection("Discord.exe", "discord.com", "162.159.135.232", 443),
            new RuleDecision { Mode = RoutingMode.Desync, Reason = "default" });

        var back = WatchEntry.Parse(entry.ToLine());

        Assert.NotNull(back);
        Assert.Equal(entry.Mode, back.Mode);
        Assert.Equal("discord.exe", back.Process);
        Assert.Equal("discord.com:443", back.Endpoint);
        Assert.Equal("162.159.135.232:443", back.Address);
        Assert.Equal("default", back.Rule);
        Assert.Equal(entry.Time.ToUnixTimeMilliseconds(), back.Time.ToUnixTimeMilliseconds());
    }

    /// <summary>Без имени — адрес; табуляция в правиле не рвёт строку.</summary>
    [Fact]
    public void WithoutANameTheAddressIsShownAndTabsAreHarmless()
    {
        var entry = WatchEntry.From(
            Connection("game.exe", null, "203.0.113.5", 7777, ProtocolKind.Udp),
            new RuleDecision { Mode = RoutingMode.Direct, Reason = "правило\tс табуляцией" });

        var back = WatchEntry.Parse(entry.ToLine());

        Assert.NotNull(back);
        Assert.Equal("203.0.113.5:7777", back.Endpoint);
        Assert.Equal("udp", back.Protocol);
        Assert.Equal("правило с табуляцией", back.Rule);
    }

    [Fact]
    public void SessionLinesAndGarbageAreNotEntries()
    {
        Assert.Null(WatchEntry.Parse(WatchEntry.Session(DateTimeOffset.Now, WatchEntry.Started + ": режим «Гибрид»")));
        Assert.Null(WatchEntry.Parse("обрывок строки"));
        Assert.Null(WatchEntry.Parse(string.Empty));
    }

    /// <summary>
    /// Сводка считает по программам и называет главные назначения каждой
    /// (владелец 10.10: «и отдельно по программам, и отдельно полностью журнал»).
    /// </summary>
    [Fact]
    public void TheSummaryCountsByProgram()
    {
        var lines = new List<string> { WatchEntry.Session(DateTimeOffset.Now, WatchEntry.Started + ": режим «Гибрид», правил 70") };

        for (int i = 0; i < 3; i++)
            lines.Add(WatchEntry.From(Connection("chrome.exe", "youtube.com", "142.250.74.46", 443), new RuleDecision { Mode = RoutingMode.Desync, Reason = "youtube" }).ToLine());

        lines.Add(WatchEntry.From(Connection("chrome.exe", "chatgpt.com", "104.18.32.47", 443), new RuleDecision { Mode = RoutingMode.Proxy, Reason = "openai" }).ToLine());
        lines.Add(WatchEntry.From(Connection("Telegram.exe", null, "149.154.167.220", 443), new RuleDecision { Mode = RoutingMode.Direct, Reason = "default" }).ToLine());

        var text = WatchEntry.Summarize(lines);

        Assert.Contains("сеансов 1", text);
        Assert.Contains("соединений 5", text);
        Assert.Matches(@"chrome\.exe\s+4\s+1\s+3\s+0\s+0", text);
        Assert.Matches(@"telegram\.exe\s+1\s+0\s+0\s+1", text);
        Assert.Contains("3  десинк    youtube.com:443", text);
        Assert.Contains("149.154.167.220:443", text);
    }

    private static readonly WatchContext Engine = new(["198.18.0.0/15", "fc00::/18"], [IPAddress.Parse("138.124.32.99")]);

    /// <summary>
    /// Подставной адрес движка — туннель как факт, и IPv6 тоже, хоть правила
    /// и зовут fc00::/7 домашней сетью. Замер 10.10: api.anthropic.com на
    /// <c>[fc00::b]</c> писался «напрямую, локальная сеть» 13 раз.
    /// </summary>
    [Theory]
    [InlineData("198.18.0.11")]
    [InlineData("fc00::b")]
    public void AFakeAddressIsTheTunnel(string address)
    {
        var entry = WatchEntry.From(
            Connection("claude.exe", "api.anthropic.com", address, 443),
            new RuleDecision { Mode = RoutingMode.Direct, Reason = "локальная сеть (жёсткое исключение)" },
            Engine);

        Assert.Equal(WatchRoute.Proxy, entry.Mode);
        Assert.Contains("подставной адрес", entry.Rule);
        Assert.True(entry.Routed);
    }

    /// <summary>Без поднятого движка подставного диапазона нет — 198.18.x тогда чужой.</summary>
    [Fact]
    public void WithoutTheEngineAFakeRangeIsNotTrusted()
    {
        var entry = WatchEntry.From(
            Connection("claude.exe", null, "fc00::b", 443),
            new RuleDecision { Mode = RoutingMode.Direct, Reason = "локальная сеть (жёсткое исключение)" });

        Assert.Equal(WatchRoute.Local, entry.Mode);
    }

    /// <summary>Мультикаст — поиск устройств, а не десинк (Spotify, Steam, ChatGPT 10.10).</summary>
    [Theory]
    [InlineData("239.255.255.250", (ushort)1900)]
    [InlineData("224.0.0.251", (ushort)5353)]
    [InlineData("ff02::fb", (ushort)5353)]
    [InlineData("255.255.255.255", (ushort)67)]
    public void MulticastIsLocal(string address, ushort port)
    {
        var entry = WatchEntry.From(
            Connection("spotify.exe", null, address, port, ProtocolKind.Udp),
            new RuleDecision { Mode = RoutingMode.Desync, Reason = "default" },
            Engine);

        Assert.Equal(WatchRoute.Local, entry.Mode);
        Assert.Equal("локально", entry.ModeWord);
        Assert.False(entry.Routed);
    }

    /// <summary>Соединения sing-box — движок: к серверу VPN или выход из туннеля, не десинк.</summary>
    [Fact]
    public void TheEnginesOwnConnectionsAreTheEngine()
    {
        var server = WatchEntry.From(
            Connection("sing-box.exe", null, "138.124.32.99", 443),
            new RuleDecision { Mode = RoutingMode.Desync, Reason = "default" },
            Engine);

        var other = WatchEntry.From(
            Connection("sing-box.exe", null, "72.56.93.144", 443),
            new RuleDecision { Mode = RoutingMode.Desync, Reason = "default" },
            Engine);

        Assert.Equal(WatchRoute.Engine, server.Mode);
        Assert.Contains("сервером VPN", server.Rule);
        Assert.Equal(WatchRoute.Engine, other.Mode);
        Assert.DoesNotContain("сервером VPN", other.Rule);

        Assert.Equal(WatchRoute.Engine, WatchEntry.Parse(server.ToLine())!.Mode);
    }

    /// <summary>
    /// «Скопировать домены» и «Скопировать IP» (владелец 10.10): без повторов,
    /// без локального, движка и подставных адресов — их в маршрут не пишут.
    /// </summary>
    [Fact]
    public void CopiedHostsAndAddressesAreOnlyTheOutside()
    {
        var any = new RuleDecision { Mode = RoutingMode.Desync, Reason = "default" };

        WatchEntry[] entries =
        [
            WatchEntry.From(Connection("chrome.exe", "youtube.com", "142.250.74.46", 443), any, Engine),
            WatchEntry.From(Connection("chrome.exe", "YouTube.com", "142.250.74.46", 443), any, Engine),
            WatchEntry.From(Connection("chrome.exe", "i.ytimg.com", "142.250.74.14", 443), any, Engine),
            WatchEntry.From(Connection("game.exe", null, "2a00:1450:4010:c05::64", 443), any, Engine),
            WatchEntry.From(Connection("game.exe", null, "9.9.9.9", 7777), any, Engine),
            WatchEntry.From(Connection("claude.exe", "api.anthropic.com", "198.18.0.11", 443), any, Engine),
            WatchEntry.From(Connection("spotify.exe", null, "239.255.255.250", 1900), any, Engine),
            WatchEntry.From(Connection("spotify.exe", "printer.local", "192.168.1.145", 8008), any, Engine),
            WatchEntry.From(Connection("sing-box.exe", null, "138.124.32.99", 443), any, Engine),
        ];

        Assert.Equal(["api.anthropic.com", "i.ytimg.com", "youtube.com"], WatchEntry.HostsOf(entries));
        Assert.Equal(["9.9.9.9", "142.250.74.14", "142.250.74.46", "2a00:1450:4010:c05::64"], WatchEntry.AddressesOf(entries));
    }

    /// <summary>Правило коротко — для таблицы и отбора по правилу.</summary>
    [Theory]
    [InlineData("#47 domain www.google.com в списке config/lists/google.txt", "#47 google")]
    [InlineData("#15 domain api.modrinth.com ~ *.modrinth.com", "#15 *.modrinth.com")]
    [InlineData("#145 ip 8.8.8.8 в списке config/lists/ipset-ru.txt", "#145 ipset-ru")]
    [InlineData("#9 ip 10.1.2.3 in 10.0.0.0/8", "#9 10.0.0.0/8")]
    [InlineData("#3 process name ~ Discord.exe", "#3 Discord.exe")]
    [InlineData("default", "по умолчанию")]
    [InlineData("локальная сеть (жёсткое исключение)", "локальная сеть (жёсткое исключение)")]
    public void TheRuleIsShortened(string rule, string shown)
    {
        Assert.Equal(shown, WatchEntry.ShortRule(rule));
    }

    /// <summary>Точно — подставной адрес, движок, локальное; остальное — ответ правил.</summary>
    [Fact]
    public void CertaintyIsWhatTheWatchKnowsItself()
    {
        var rule = new RuleDecision { Mode = RoutingMode.Proxy, Reason = "openai" };

        Assert.True(WatchEntry.From(Connection("codex.exe", "chatgpt.com", "198.18.0.24", 443), rule, Engine).Certain);
        Assert.True(WatchEntry.From(Connection("sing-box.exe", null, "138.124.32.99", 443), rule, Engine).Certain);
        Assert.True(WatchEntry.From(Connection("svchost.exe", null, "192.168.1.1", 53), rule, Engine).Certain);
        Assert.False(WatchEntry.From(Connection("chrome.exe", "chatgpt.com", "104.18.32.47", 443), rule, Engine).Certain);
    }

    /// <summary>Сайт — два уровня, у зон вида co.uk — три (10.10: «co.uk» в «Сайтах»).</summary>
    [Theory]
    [InlineData("rr4---sn-nx8xon3t-83vl.googlevideo.com", "googlevideo.com")]
    [InlineData("kws2.pclead.co.uk", "pclead.co.uk")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("shop.example.com.br", "example.com.br")]
    [InlineData("api.anthropic.com", "anthropic.com")]
    [InlineData("t.co", "t.co")]
    [InlineData("discord.media", "discord.media")]
    public void TheSiteIsTheRegistrablePart(string host, string site)
    {
        Assert.Equal(site, WatchEntry.SiteOf(host));
    }

    [Fact]
    public void WithoutAJournalTheSummarySaysSo()
    {
        Assert.Contains("не включали", WatchEntry.Summarize([]));
    }
}
