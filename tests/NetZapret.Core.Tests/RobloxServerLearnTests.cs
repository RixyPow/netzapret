using System.Net;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Адреса игровых серверов Roblox из его журнала — в список части «Игровые серверы (адреса)» (21.10).
/// </summary>
public sealed class RobloxServerLearnTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nz-roblox-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Строки — из журнала игрока владельца 21.10 (номера плейсов и адреса оттуда,
    // идентификатор сервера заменён). Сервер Roblox: «Joining» с внутренним 10.x,
    // UDMUX и «Connection accepted» — 128.116.x. Плейс на Amazon: и «Joining»,
    // и «Connection accepted» — 18.159.131.236.
    private const string Log = """
        2026-10-21T14:29:20.128Z,436.128510,72e0,6 [FLog::Output] ! Joining game 'id-1' place 119958473240233 at 10.206.17.41
        2026-10-21T14:29:20.144Z,436.144470,8d94,7 [FLog::Network] UDMUX Address = 128.116.5.33, Port = 49920 | RCC Server Address = 10.206.17.41, Port = 49920
        2026-10-21T14:29:20.755Z,436.755615,1a68,6,Info [DFLog::NetworkClient] Connection accepted from 128.116.5.33|49920
        2026-10-21T14:29:45.751Z,461.751526,2d04,6 [FLog::Output] ! Joining game 'id-2' place 139386728420706 at 18.159.131.236
        2026-10-21T14:29:48.504Z,464.504547,72e0,6,Info [DFLog::NetworkClient] Connection accepted from 18.159.131.236|54162
        2026-10-21T14:30:05.975Z,481.975403,1a68,7 [FLog::Network] Lost connection to 18.159.131.236|54162: ID_CONNECTION_LOST
        2026-10-21T14:36:00.000Z,900.000000,1a68,6 [FLog::Output] ! Joining game 'id-3' place 1 at 54.93.176.232
        """;

    [Fact]
    public void Server_addresses_are_read_from_the_log_without_internal_ones()
    {
        var addresses = RobloxServerLearn.Addresses(Log);

        Assert.Equal(
            [IPAddress.Parse("128.116.5.33"), IPAddress.Parse("18.159.131.236"), IPAddress.Parse("54.93.176.232")],
            addresses);
    }

    [Theory]
    [InlineData("10.206.17.41", false)]
    [InlineData("172.19.0.1", false)]
    [InlineData("192.168.1.120", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("18.159.131.236", true)]
    public void Only_public_addresses_go_into_the_tunnel(string address, bool expected) =>
        Assert.Equal(expected, RobloxServerLearn.IsPublic(IPAddress.Parse(address)));

    /// <summary>
    /// Сети Roblox уже в списке; дописываются /20 плейсов на Amazon — и один раз:
    /// второй проход по тому же журналу ничего не добавляет.
    /// </summary>
    [Fact]
    public void Servers_outside_the_list_are_added_as_networks_once()
    {
        var list = Path.Combine(_dir, "roblox-network.txt");
        File.WriteAllLines(list, ["# Roblox — сети игровых серверов (AS22697).", "128.116.0.0/17"]);

        var added = RobloxServerLearn.Learn(Log, list, new DateTime(2026, 10, 21));

        Assert.Equal(["18.159.128.0/20", "54.93.176.0/20"], added);
        Assert.Equal(
            ["# Roblox — сети игровых серверов (AS22697).", "128.116.0.0/17",
             "# из журнала Roblox, 21.10.2026", "18.159.128.0/20", "54.93.176.0/20"],
            File.ReadAllLines(list));

        Assert.Empty(RobloxServerLearn.Learn(Log, list));
    }

    [Fact]
    public void Learning_happens_only_when_the_servers_go_through_the_vpn()
    {
        var rules = UserRulesFile.Load(Path.Combine(_dir, "rules.user.yaml"));
        Assert.False(RobloxServerLearn.RoutedToVpn(rules.Entries));

        rules.Set(MatchKind.IpSet, RobloxServerLearn.ListPath, RoutingMode.Desync);
        Assert.False(RobloxServerLearn.RoutedToVpn(rules.Entries));

        rules.Set(MatchKind.IpSet, RobloxServerLearn.ListPath, RoutingMode.Proxy);
        Assert.True(RobloxServerLearn.RoutedToVpn(rules.Entries));
    }

    /// <summary>Журналы игрока моложе трёх дней, без журналов падений и Studio.</summary>
    [Fact]
    public void Only_recent_player_logs_are_read()
    {
        var logs = Path.Combine(_dir, "Roblox", "logs");
        Directory.CreateDirectory(logs);

        var now = new DateTime(2026, 10, 21, 18, 0, 0);
        string Make(string name, DateTime written)
        {
            var path = Path.Combine(logs, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, written);
            return path;
        }

        var fresh = Make("0.743.0_20261021T142204Z_Player_30901_last.log", now.AddHours(-1));
        Make("0.743.0_20261021T142205Z_Player_8DB92_CrashHandler_last.log", now.AddHours(-1));
        Make("0.743.506_20261021T075730Z_Studio_91816_last.log", now.AddHours(-1));
        Make("0.700.0_20261001T100000Z_Player_AAAAA_last.log", now.AddDays(-20));

        Assert.Equal([fresh], RobloxServerLearn.LogPaths(_dir, now));
    }

    /// <summary>Тот же список, что у части в каталоге: иначе дописанное не вело бы никуда.</summary>
    [Fact]
    public void The_list_is_the_one_of_the_game_servers_part() =>
        Assert.Contains(ServiceCatalog.All.SelectMany(s => s.Parts),
            p => p.List == RobloxServerLearn.ListPath && p.ByAddress);
}
