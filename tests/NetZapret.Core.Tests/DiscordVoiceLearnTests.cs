using System.Net;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Адреса голоса Discord из его журнала — в список части «Звук голоса (адреса)» (04.10).
/// </summary>
public sealed class DiscordVoiceLearnTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nz-voice-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // «Creating connection to адрес:порт» — как в renderer_js.log владельца 04.10
    // (104.29.148.52 оттуда); обрамление строк и адреса 35.217.11.x выдуманы.
    private const string Log = """
        [2026-10-04 19:12:01.123] [info] [RTCConnection(1)] Creating connection to 104.29.148.52:19329 with audio ssrc: 1
        [2026-10-04 19:12:01.456] [info] Connection state change: CONNECTING => CONNECTED
        [2026-10-04 19:40:02.000] [info] [RTCConnection(2)] Creating connection to 35.217.11.20:50003 with audio ssrc: 2
        [2026-10-04 19:40:03.000] [info] [RTCConnection(3)] Creating connection to 35.217.11.77:50005 with audio ssrc: 3
        """;

    [Fact]
    public void Voice_addresses_are_read_from_the_log()
    {
        var addresses = DiscordVoiceLearn.Addresses(Log);

        Assert.Equal(["104.29.148.52", "35.217.11.20", "35.217.11.77"], addresses.Select(a => a.ToString()));
    }

    /// <summary>Покрытое списком не дописывается; два адреса одной сети — одна строка /20.</summary>
    [Fact]
    public void Only_uncovered_networks_are_missing()
    {
        var missing = DiscordVoiceLearn.Missing(DiscordVoiceLearn.Addresses(Log), ["# Cloudflare", "104.29.128.0/19"]);

        Assert.Equal(["35.217.0.0/20"], missing);
    }

    [Fact]
    public void Learning_appends_once()
    {
        var list = Path.Combine(_dir, "discord-voice-net.txt");
        File.WriteAllText(list, "104.29.128.0/19");

        var added = DiscordVoiceLearn.Learn(Log, list, new DateTime(2026, 10, 4));

        Assert.Equal(["35.217.0.0/20"], added);
        Assert.Equal(["104.29.128.0/19", "# из журнала Discord, 04.10.2026", "35.217.0.0/20"], File.ReadAllLines(list));

        Assert.Empty(DiscordVoiceLearn.Learn(Log, list));
    }

    [Fact]
    public void Learning_happens_only_when_the_voice_goes_through_the_vpn()
    {
        var rules = UserRulesFile.Load(Path.Combine(_dir, "rules.user.yaml"));
        Assert.False(DiscordVoiceLearn.RoutedToVpn(rules.Entries));

        rules.Set(MatchKind.IpSet, DiscordVoiceLearn.ListPath, RoutingMode.Desync);
        Assert.False(DiscordVoiceLearn.RoutedToVpn(rules.Entries));

        rules.Set(MatchKind.IpSet, DiscordVoiceLearn.ListPath, RoutingMode.Proxy);
        Assert.True(DiscordVoiceLearn.RoutedToVpn(rules.Entries));
    }

    /// <summary>Хвост: недописанная строка ждёт конца, новый журнал читается с начала.</summary>
    [Fact]
    public void The_tail_reads_whole_lines_only()
    {
        var log = Path.Combine(_dir, "renderer_js.log");
        var tail = new LogTail();

        File.WriteAllText(log, "first line\nCreating connection to 104.29.");
        Assert.Equal("first line\n", tail.ReadNew([log]));

        File.AppendAllText(log, "148.52:19329\n");
        Assert.Contains("Creating connection to 104.29.148.52:19329", tail.ReadNew([log]));
        Assert.Equal(string.Empty, tail.ReadNew([log]));

        File.WriteAllText(log, "new\n");
        Assert.Equal("new\n", tail.ReadNew([log]));
    }

    [Fact]
    public void Logs_of_all_three_clients_are_watched()
    {
        var paths = DiscordVoiceLearn.LogPaths(@"C:\Users\x\AppData\Roaming");

        Assert.Contains(@"C:\Users\x\AppData\Roaming\discord\logs\renderer_js.log", paths);
        Assert.Contains(@"C:\Users\x\AppData\Roaming\discordptb\logs\renderer_js.log", paths);
        Assert.Contains(@"C:\Users\x\AppData\Roaming\discordcanary\logs\renderer_js.log", paths);
    }

    [Fact]
    public void Ipv6_is_not_taken()
    {
        Assert.Empty(DiscordVoiceLearn.Addresses("Creating connection to [2606:4700::1]:19305"));
        Assert.DoesNotContain(IPAddress.IPv6Loopback, DiscordVoiceLearn.Addresses(Log));
    }

    /// <summary>Разброс Google Cloud из журнала владельца 04.10 — 35.217.6–63 — закрывают четыре сети /20.</summary>
    [Fact]
    public void Google_spread_fits_four_networks()
    {
        var log = string.Join("\n", Enumerable.Range(6, 58).Select(n => $"Creating connection to 35.217.{n}.10:50000"));

        var missing = DiscordVoiceLearn.Missing(DiscordVoiceLearn.Addresses(log), ["104.29.128.0/19"]);

        Assert.Equal(["35.217.0.0/20", "35.217.16.0/20", "35.217.32.0/20", "35.217.48.0/20"], missing);
    }

    /// <summary>Выключатель включён и у настроек, где его ещё нет, и выключается.</summary>
    [Fact]
    public void Learning_is_on_by_default_and_can_be_turned_off()
    {
        var path = Path.Combine(_dir, "netzapret.json");
        File.WriteAllText(path, "{}");

        Assert.True(AppSettings.Load(path).LearnDiscordVoice);

        (AppSettings.Load(path) with { LearnDiscordVoice = false }).Save(path);

        Assert.False(AppSettings.Load(path).LearnDiscordVoice);
    }

    /// <summary>Обзор для nz voice: по адресу — сколько раз, последний раз и чем покрыт.</summary>
    [Fact]
    public void The_survey_counts_dates_and_coverage()
    {
        // Строки — как в журнале владельца 05.10.
        const string log = """
            [2026-10-03 09:03:14.100] [info]  [Connection(default)] Creating connection to 35.217.45.215:50003 with audio ssrc: 1
            [2026-10-05 00:37:31.461] [info]  [Connection(default)] Creating connection to 104.29.146.252:19298 with audio ssrc: 3523
            [2026-10-05 00:40:02.000] [info]  [Connection(default)] Creating connection to 35.217.45.215:50005 with audio ssrc: 2
            """;

        var survey = DiscordVoiceLearn.Survey(log, ["104.29.128.0/19"]);

        Assert.Equal(2, survey.Count);
        Assert.Equal(("35.217.45.215", 2, "2026-10-05 00:40:02", (string?)null),
            (survey[0].Address.ToString(), survey[0].Seen, survey[0].LastSeen, survey[0].CoveredBy));
        Assert.Equal("104.29.128.0/19", survey[1].CoveredBy);
    }

    /// <summary>
    /// Текст для nz voice и отчёта: журнал из выдуманного %APPDATA%, список и правила
    /// из выдуманного корня — ничего не дописывается.
    /// </summary>
    [Fact]
    public void The_report_reads_the_log_and_writes_nothing()
    {
        var appData = Path.Combine(_dir, "appdata");
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(Path.Combine(appData, "discord", "logs"));
        Directory.CreateDirectory(Path.Combine(root, "config", "lists"));

        File.WriteAllText(Path.Combine(appData, "discord", "logs", "renderer_js.log"), Log);

        var list = Path.Combine(root, DiscordVoiceLearn.ListPath);
        File.WriteAllText(list, "104.29.128.0/19\n");

        var (text, found) = DiscordVoiceLearn.Report(appData, root);

        Assert.True(found);
        Assert.Contains("адресов звука: 3", text);
        Assert.Contains("в списке по 104.29.128.0/19: 1", text);
        Assert.Contains("вне списка: 2", text);
        Assert.Contains("сторож дописал бы: 35.217.0.0/20", text);
        Assert.Contains("не через VPN — сторож не дописывает", text);
        Assert.Equal("104.29.128.0/19\n", File.ReadAllText(list));
    }

    [Fact]
    public void Without_a_log_the_report_says_so()
    {
        var (text, found) = DiscordVoiceLearn.Report(Path.Combine(_dir, "empty"), _dir);

        Assert.False(found);
        Assert.Contains("журнала Discord нет", text);
    }
}
