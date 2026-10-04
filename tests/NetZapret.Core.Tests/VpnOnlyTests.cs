using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// «Всё через VPN» из трея и возврат к прежнему (обсуждение №17, 04.10).
/// </summary>
public sealed class VpnOnlyTests
{
    private static AppSettings With(bool desync, bool tunnel) =>
        AppSettings.Fresh.With(new EngineChoice { Desync = desync, Tunnel = tunnel }) with { SubscriptionUrl = "https://example.invalid/sub" };

    [Fact]
    public void Turning_on_drops_the_desync_and_keeps_the_tunnel()
    {
        var on = VpnOnly.Toggle(With(desync: true, tunnel: true));

        Assert.True(VpnOnly.IsOn(on));
        Assert.True(on.Engines.TunnelTakesAll);
    }

    [Fact]
    public void Turning_off_returns_what_was_there()
    {
        var before = With(desync: true, tunnel: false);
        var back = VpnOnly.Toggle(VpnOnly.Toggle(before));

        Assert.False(VpnOnly.IsOn(back));
        Assert.True(back.Engines.Desync);
        Assert.False(back.Engines.Tunnel);
        Assert.Null(back.DesyncBeforeVpnOnly);
        Assert.Null(back.TunnelBeforeVpnOnly);
    }

    /// <summary>Набранное руками на «Главной» — то же состояние; выключение возвращает десинк.</summary>
    [Fact]
    public void Set_by_hand_is_on_and_turns_off_to_the_desync()
    {
        var byHand = With(desync: false, tunnel: true);

        Assert.True(VpnOnly.IsOn(byHand));

        var off = VpnOnly.Toggle(byHand);

        Assert.True(off.Engines.Desync);
        Assert.True(off.Engines.Tunnel);
    }

    [Fact]
    public void Without_an_exit_it_cannot_be_turned_on()
    {
        var noExit = AppSettings.Fresh with { SubscriptionUrl = null, WarpEnabled = false, KeysEnabled = false };

        Assert.False(VpnOnly.CanTurnOn(noExit));
        Assert.True(VpnOnly.CanTurnOn(With(desync: true, tunnel: true)));
    }
}
