using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Автонастройка прокси Telegram (03.11): замер решает прямой путь и порядок доменов.</summary>
public sealed class TgProxyTuneTests
{
    [Fact]
    public void Flowseal_list_decodes_the_way_the_proxy_shows_it()
    {
        // Первые строки списка 03.11 и то, что прокси 2.6.0 напечатал в шапке.
        Assert.Equal("pclead.co.uk", TgProxyTune.Decode("virkgj.com"));
        Assert.Equal(["pclead.co.uk", "offshor.co.uk"], TgProxyTune.Parse("# заметка\nvirkgj.com\r\nvmmzovy.com\n\nvirkgj.com\n"));
    }

    [Fact]
    public void Tuning_decides_direct_path_and_puts_live_domains_first()
    {
        var manual = new TgWsProxyRoutes(DirectMain: false, DirectOther: true, WorkerDomains: "w.workers.dev");
        var tuning = new TgProxyTuning(DateTimeOffset.Now, true, 410,
            [.. Enumerable.Range(1, 7).Select(i => new TgBridge($"d{i}.co.uk", 100 + i))], 20);

        var routes = TgProxyTune.Apply(manual, tuning);

        Assert.True(routes.DirectMain);
        Assert.True(routes.DirectOther);
        Assert.Equal(["d1.co.uk", "d2.co.uk", "d3.co.uk", "d4.co.uk", "d5.co.uk"], routes.CfDomains);

        var arguments = TgWsProxy.Arguments(1443, routes);
        int at = arguments.ToList().IndexOf("--cf-domain");

        Assert.True(at > 0);
        Assert.Equal("d1.co.uk,d2.co.uk,d3.co.uk,d4.co.uk,d5.co.uk", arguments[at + 1]);
        Assert.Contains("--default-domains", arguments);
        Assert.Contains("2:149.154.167.220", arguments);
    }

    [Fact]
    public void Dead_direct_path_is_switched_off_even_if_set_by_hand()
    {
        var routes = TgProxyTune.Apply(new TgWsProxyRoutes(DirectMain: true),
            new TgProxyTuning(DateTimeOffset.Now, false, null, [], 20));

        Assert.False(routes.DirectMain);
        Assert.DoesNotContain("--cf-domain", TgWsProxy.Arguments(1443, routes));
    }

    [Fact]
    public void Result_survives_a_round_trip()
    {
        var now = DateTimeOffset.Now;
        var path = Path.Combine(Path.GetTempPath(), $"nz-tg-tune-{Guid.NewGuid():N}.json");
        var tuning = new TgProxyTuning(now, true, 410, [new("onedaychamp.co.uk", 130)], 20);

        try
        {
            TgProxyTune.Save(tuning, path);
            var back = TgProxyTune.Load(path);

            Assert.NotNull(back);
            Assert.Equal("onedaychamp.co.uk", back!.Bridges[0].Domain);
            Assert.Contains("1 из 20", TgProxyTune.Describe(back));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
