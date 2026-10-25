using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Запасной резолвер движка (25.10): умер Comss DNS, и при пути «напрямую»
/// каждое имя ждало 11–12 с, а «Главная» и «Диагностика» молчали.
/// </summary>
public sealed class DnsSpareTests
{
    [Theory]
    [InlineData("83.220.169.155", "8.8.4.4")]
    [InlineData("9.9.9.10", "8.8.4.4")]
    [InlineData("8.8.4.4", "1.1.1.1")]
    [InlineData("8.8.8.8", "1.1.1.1")]
    [InlineData(null, "8.8.4.4")]
    public void TheSpareBelongsToSomeoneElse(string? primary, string spare) =>
        Assert.Equal(spare, DnsSpare.For(primary).TlsAddress);

    [Fact]
    public void AResolverIsNamedWithItsAddress()
    {
        Assert.Equal("Comss.one (83.220.169.155)", DnsSpare.Describe("83.220.169.155"));
        Assert.Equal("10.0.0.53", DnsSpare.Describe("10.0.0.53"));
    }

    /// <summary>Надзор узнаёт запасной из конфига — того, с которым поднят движок.</summary>
    [Fact]
    public void TheSpareIsReadFromTheConfig()
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules: []
            """);

        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [], new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServer = "83.220.169.155",
            DnsServerName = "dns.comss.one",
            DnsSpare = DnsSpare.For("83.220.169.155"),
        }).Json;

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-spare-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, json);

            var upstream = DnsUpstream.FromConfig(path)!;

            Assert.Equal("83.220.169.155", upstream.Server);
            Assert.Equal("8.8.4.4", upstream.Spare!.Server);
            Assert.Equal("dns.google", upstream.Spare.Name);
            Assert.Equal("https", upstream.Spare.Type);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true, false, false, false, DnsLadder.Step.Direct)]
    [InlineData(true, true, true, true, DnsLadder.Step.Direct)]
    [InlineData(false, true, true, true, DnsLadder.Step.Tcp)]
    [InlineData(false, false, true, true, DnsLadder.Step.Spare)]
    [InlineData(false, false, false, true, DnsLadder.Step.Tunnel)]
    [InlineData(false, false, false, false, DnsLadder.Step.None)]
    public void TheLadderGoesDirectTcpSpareTunnel(bool direct, bool tcp, bool spare, bool tunnel, DnsLadder.Step expected) =>
        Assert.Equal(expected, DnsLadder.Choose(direct, tcp, spare, tunnel));

    [Fact]
    public void TheNoticeSaysWhoIsAsked()
    {
        const string comss = "Comss.one (83.220.169.155)";
        const string google = "Google (8.8.4.4)";

        Assert.Null(DnsLadder.Notice(DnsLadder.Step.Direct, comss, google));
        Assert.Equal($"резолвер {comss} не отвечает — имена спрашиваются у запасного {google}",
            DnsLadder.Notice(DnsLadder.Step.Spare, comss, google));
        Assert.Contains($"запасной {google} тоже", DnsLadder.Notice(DnsLadder.Step.None, comss, google));
        Assert.DoesNotContain("запасной", DnsLadder.Notice(DnsLadder.Step.None, comss, null));
    }

    /// <summary>«Диагностика» судит по замеру надзора, а не по настройкам.</summary>
    [Fact]
    public void DoctorTellsWhatTheEngineResolverDoes()
    {
        static SupervisorState State(string? notice, bool down, int pid) => new()
        {
            SupervisorProcessId = pid,
            StartedAt = DateTimeOffset.Now,
            Services =
            [
                new ServiceState { Name = "sing-box", Health = ServiceHealth.Healthy, DnsNotice = notice, DnsDown = down },
            ],
        };

        int alive = Environment.ProcessId;

        Assert.Null(Doctor.EngineDns(State(null, false, alive)));
        Assert.Null(Doctor.EngineDns(null));

        var spare = Doctor.EngineDns(State("резолвер X не отвечает — имена спрашиваются у запасного Y", false, alive))!;
        Assert.Equal(DoctorLevel.Warn, spare.Level);
        Assert.StartsWith("Резолвер X не отвечает", spare.Text);

        Assert.Equal(DoctorLevel.Bad, Doctor.EngineDns(State("резолвер X не отвечает — имена не разрешаются", true, alive))!.Level);
    }
}
