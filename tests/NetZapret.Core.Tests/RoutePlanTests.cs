using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Автоподбор маршрута (27.10): по симптому проверки блокировок — что пробовать
/// и в каком порядке; рецепты — пробным winws2 на своих портах.
/// </summary>
public sealed class RoutePlanTests
{
    private static TargetReport Report(BlockKind kind, ProbeOutcome? quic = null) => new()
    {
        Host = "example.org",
        Tcp = new ProbeOutcome { Ok = true },
        Tls = new ProbeOutcome { Ok = kind == BlockKind.None },
        Http = new ProbeOutcome { Ok = true },
        Data = new ProbeOutcome { Ok = kind == BlockKind.None },
        Kind = kind,
        Quic = quic,
    };

    private static RouteTry[] Kinds(RoutePlan plan) => plan.Steps.Select(s => s.Kind).ToArray();

    [Fact]
    public void A_cut_handshake_tries_direct_then_recipes_then_vpn()
    {
        var plan = RoutePlan.For(Report(BlockKind.TlsDpi), RoutingMode.Desync, desync: true, tunnel: true);

        Assert.Equal([RouteTry.Direct, RouteTry.Recipes, RouteTry.Vpn], Kinds(plan));
        Assert.Same(RoutePlan.ClientHelloOrder, plan.Steps[1].Recipes);
    }

    /// <summary>Белому списку щит не поможет — сразу подделка разрешённого имени.</summary>
    [Fact]
    public void A_white_list_skips_direct_and_starts_with_an_allowed_name()
    {
        var plan = RoutePlan.For(Report(BlockKind.TlsWhiteOnly), RoutingMode.Desync, desync: true, tunnel: true);

        Assert.Equal([RouteTry.Recipes, RouteTry.Vpn], Kinds(plan));
        Assert.Equal("seqovl-4pda", plan.Steps[0].Recipes[0]);
    }

    /// <summary>Имя «напрямую» сперва пробует десинк пресета, потом рецепты.</summary>
    [Fact]
    public void A_direct_name_that_freezes_tries_the_preset_first()
    {
        var plan = RoutePlan.For(Report(BlockKind.Stall), RoutingMode.Direct, desync: true, tunnel: true);

        Assert.Equal([RouteTry.Preset, RouteTry.Recipes, RouteTry.Vpn], Kinds(plan));
        Assert.Same(RoutePlan.WhiteNameOrder, plan.Steps[1].Recipes);
    }

    [Theory]
    [InlineData(BlockKind.TlsByAddress)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.HttpsPort)]
    public void A_cut_address_goes_straight_to_vpn(BlockKind kind)
    {
        var plan = RoutePlan.For(Report(kind), RoutingMode.Desync, desync: true, tunnel: true);

        Assert.Equal([RouteTry.Vpn], Kinds(plan));
        Assert.Contains("другой адрес", plan.Advice);
    }

    [Fact]
    public void A_country_refusal_offers_vpn_and_names_the_relay()
    {
        var plan = RoutePlan.For(Report(BlockKind.GeoBlock), RoutingMode.Desync, desync: true, tunnel: true);

        Assert.Equal([RouteTry.Vpn], Kinds(plan));
        Assert.Contains("Zapret DNS", plan.Advice);
    }

    [Theory]
    [InlineData(BlockKind.Dns)]
    [InlineData(BlockKind.Sinkhole)]
    [InlineData(BlockKind.BrokenCname)]
    [InlineData(BlockKind.ForeignFakeIp)]
    [InlineData(BlockKind.Handshake)]
    public void Where_a_route_cannot_help_there_are_no_steps(BlockKind kind)
    {
        var plan = RoutePlan.For(Report(kind), RoutingMode.Desync, desync: true, tunnel: true);

        Assert.Empty(plan.Steps);
        Assert.Contains(Report(kind).Remedy(), plan.Advice);
    }

    [Fact]
    public void An_open_name_changes_nothing_but_a_silent_quic_is_named()
    {
        Assert.Null(RoutePlan.For(Report(BlockKind.None), RoutingMode.Desync, true, true).Advice);

        var silent = RoutePlan.For(Report(BlockKind.None, new ProbeOutcome { Ok = false }), RoutingMode.Desync, true, true);

        Assert.Empty(silent.Steps);
        Assert.Contains("QUIC", silent.Advice);
    }

    [Fact]
    public void Without_desync_or_tunnel_their_steps_are_left_out()
    {
        Assert.Equal([RouteTry.Vpn], Kinds(RoutePlan.For(Report(BlockKind.TlsDpi), RoutingMode.Desync, desync: false, tunnel: true)));
        Assert.Equal([RouteTry.Direct, RouteTry.Recipes], Kinds(RoutePlan.For(Report(BlockKind.TlsDpi), RoutingMode.Desync, desync: true, tunnel: false)));

        // Уже через VPN — пробовать его ещё раз незачем.
        Assert.Empty(RoutePlan.For(Report(BlockKind.TunnelFailed), RoutingMode.Proxy, true, true).Steps);
    }

    /// <summary>Порядки рецептов — все рецепты каталога, кроме «не трогать», без повторов.</summary>
    [Fact]
    public void Both_recipe_orders_cover_the_catalog()
    {
        var catalog = RecipeCatalog.All.Where(r => r.Id != RecipeCatalog.Pass).Select(r => r.Id).Order().ToList();

        foreach (var order in new[] { RoutePlan.ClientHelloOrder, RoutePlan.WhiteNameOrder })
        {
            Assert.Equal(order.Count, order.Distinct().Count());
            Assert.Equal(catalog, order.Order().ToList());
        }
    }

    private static SupervisorState State(bool winws, bool probesOut) => new()
    {
        SupervisorProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.Now,
        Services = winws
            ? [new ServiceState { Name = "winws2", Health = ServiceHealth.Healthy, KeepsProbesOut = probesOut }]
            : [],
    };

    [Fact]
    public void Recipes_are_tried_only_beside_a_desync_that_keeps_the_probe_ports_out()
    {
        Assert.Null(SiteFixer.WhyNoTrial(State(true, true), elevated: true));
        Assert.Contains("администратора", SiteFixer.WhyNoTrial(State(true, true), elevated: false));
        Assert.Contains("не запущен", SiteFixer.WhyNoTrial(State(false, false), elevated: true));
        Assert.Contains("новой сборкой", SiteFixer.WhyNoTrial(State(true, false), elevated: true));
    }

    [Fact]
    public void The_vpn_summary_counts_the_recipes_it_tried()
    {
        var text = SiteFixer.ViaVpnSummary("example.org",
        [
            new SiteFixStep("как есть (десинк)", false, "DPI по TLS"),
            new SiteFixStep("рецепт «Разрез по имени»", false, "DPI по TLS"),
            new SiteFixStep("рецепт «Наложение приветствием 4pda»", false, "DPI по TLS"),
            new SiteFixStep("через VPN", true, "ответ 200"),
        ]);

        Assert.Contains("Рецептов десинка перепробовано 2", text);
        Assert.DoesNotContain("не пробовал", text);
    }
}
