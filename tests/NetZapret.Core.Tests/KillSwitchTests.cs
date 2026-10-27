using System.Net;
using System.Runtime.InteropServices;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Kill switch (владелец 27.10): правила и раскладка структур WFP.
/// </summary>
/// <remarks>
/// Ставить фильтры тест не смеет — без прав администратора не выйдет, а с ними
/// (CI) запер бы сеть машины. Пробный прогон отменяет транзакцию и сеть не трогает.
/// </remarks>
public sealed class KillSwitchTests
{
    private static readonly IPAddress TunV4 = IPAddress.Parse("172.19.0.1");
    private static readonly IPAddress TunV6 = IPAddress.Parse("fdfe:dcba:9876::1");

    private static IReadOnlyList<KillSwitchRule> Plan(IPAddress? v6 = null) =>
        KillSwitch.Plan([@"C:\NZ\exe\sing-box.exe", @"C:\NZ\NetZapret.exe"], TunV4, v6 ?? TunV6);

    /// <summary>Раскладка как в заголовках Windows SDK (x64): ошибись она — WFP получил бы мусор.</summary>
    [Fact]
    public void Native_structures_have_the_sizes_of_the_sdk()
    {
        Assert.Equal(16, Marshal.SizeOf<KillSwitch.Native.Value>());
        Assert.Equal(40, Marshal.SizeOf<KillSwitch.Native.FilterCondition>());
        Assert.Equal(72, Marshal.SizeOf<KillSwitch.Native.Session>());
        Assert.Equal(72, Marshal.SizeOf<KillSwitch.Native.SubLayer>());
        Assert.Equal(200, Marshal.SizeOf<KillSwitch.Native.Filter>());

        Assert.Equal(96, (int)Marshal.OffsetOf<KillSwitch.Native.Filter>(nameof(KillSwitch.Native.Filter.Weight)));
        Assert.Equal(128, (int)Marshal.OffsetOf<KillSwitch.Native.Filter>(nameof(KillSwitch.Native.Filter.Action)));
        Assert.Equal(152, (int)Marshal.OffsetOf<KillSwitch.Native.Filter>(nameof(KillSwitch.Native.Filter.Context)));
        Assert.Equal(184, (int)Marshal.OffsetOf<KillSwitch.Native.Filter>(nameof(KillSwitch.Native.Filter.EffectiveWeight)));
    }

    [Fact]
    public void Everything_else_is_blocked_with_the_lowest_weight_on_both_families()
    {
        var rules = Plan();

        foreach (bool v6 in new[] { false, true })
        {
            var family = rules.Where(r => r.V6 == v6).ToList();
            var catchAll = Assert.Single(family, r => r.Condition is null);

            Assert.False(catchAll.Permit);
            Assert.Equal(0, catchAll.Weight);
            Assert.All(family.Where(r => r != catchAll), r => Assert.True(r.Weight > 0));
        }
    }

    [Fact]
    public void The_tunnel_the_engine_and_loopback_outrank_the_dns_block_and_the_dns_block_outranks_the_lan()
    {
        var rules = Plan().Where(r => !r.V6).ToList();

        byte Weight<T>() where T : KillSwitchCondition => rules.First(r => r.Condition is T).Weight;

        var dns = rules.Single(r => r.Condition is KillSwitchCondition.RemotePort { Port: 53 });
        Assert.False(dns.Permit);

        Assert.True(Weight<KillSwitchCondition.LocalAddress>() > dns.Weight);
        Assert.True(Weight<KillSwitchCondition.App>() > dns.Weight);
        Assert.True(Weight<KillSwitchCondition.Loopback>() > dns.Weight);
        Assert.True(Weight<KillSwitchCondition.RemoteAddress>() < dns.Weight);

        var tunnel = Assert.IsType<KillSwitchCondition.LocalAddress>(rules.Single(r => r.Condition is KillSwitchCondition.LocalAddress).Condition);
        Assert.Equal(TunV4, tunnel.Address);
        Assert.Equal(32, tunnel.Prefix);

        Assert.Equal(2, rules.Count(r => r.Condition is KillSwitchCondition.App && r.Permit));
    }

    [Fact]
    public void Without_ipv6_on_the_tun_ipv6_is_still_blocked_but_has_no_tunnel_permit()
    {
        var rules = KillSwitch.Plan([@"C:\NZ\exe\sing-box.exe"], TunV4, null);
        var v6 = rules.Where(r => r.V6).ToList();

        Assert.DoesNotContain(v6, r => r.Condition is KillSwitchCondition.LocalAddress);
        Assert.Contains(v6, r => r.Condition is null && !r.Permit);
    }

    [Theory]
    [InlineData(true, false, true, true)]    // «Туннель»
    [InlineData(true, true, false, false)]   // «Гибрид»
    [InlineData(false, true, false, false)]  // «Десинк»
    [InlineData(true, false, false, false)]  // «Туннель», kill switch выключен
    public void It_is_active_only_in_tunnel_mode_when_switched_on(bool tunnel, bool desync, bool killSwitch, bool active)
    {
        var settings = new AppSettings { TunnelEnabled = tunnel, DesyncEnabled = desync, KillSwitch = killSwitch };

        Assert.Equal(active, settings.KillSwitchActive);
    }

    /// <summary>
    /// Пробный прогон через сам WFP: с правами администратора (CI) Windows обязана
    /// принять все правила; без них — отказ на входе, и проверять нечем.
    /// </summary>
    [Fact]
    public void Windows_accepts_the_rules_in_a_transaction_that_is_then_aborted()
    {
        var self = Environment.ProcessPath!;
        var rules = KillSwitch.Plan([self], TunV4, TunV6);

        // С правами администратора отказ — поломка правил: тест обязан пройти
        // без исключения. Без них WFP отказывает на начале транзакции (код 5,
        // замер 27.10), и проверять нечем.
        if (IsAdministrator())
        {
            KillSwitch.DryRun(rules);
            return;
        }

        var denied = Assert.Throws<InvalidOperationException>(() => KillSwitch.DryRun(rules));
        Assert.Contains("0x00000005", denied.Message);
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
