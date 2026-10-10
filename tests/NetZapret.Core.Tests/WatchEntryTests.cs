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

    [Fact]
    public void WithoutAJournalTheSummarySaysSo()
    {
        Assert.Contains("не включали", WatchEntry.Summarize([]));
    }
}
