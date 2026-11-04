using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>Волна туннельного эффекта на «Главной» (05.11): что проходит, что отражается, где видна.</summary>
public sealed class TunnelShapeTests
{
    private const double Barrier = 600;

    private static readonly TunnelShape.Packet AtBarrier = new(Barrier, 30, 40, 100);

    [Fact]
    public void Nothing_passes_a_closed_barrier()
    {
        Assert.Equal(0, TunnelShape.Wave([AtBarrier], Barrier + 10, Barrier, pass: 0));
        Assert.True(TunnelShape.Wave([AtBarrier], Barrier - 10, Barrier, pass: 0) > 0);
    }

    [Fact]
    public void What_passes_is_the_share_of_the_packet()
    {
        double before = TunnelShape.Wave([AtBarrier], Barrier - 1, Barrier, pass: 0.4);
        double after = TunnelShape.Wave([AtBarrier], Barrier + 1, Barrier, pass: 0.4);

        Assert.Equal(before * 0.4, after, 0.5);
    }

    [Fact]
    public void Echo_starts_only_when_the_packet_reaches_the_barrier_and_carries_what_did_not_pass()
    {
        var far = AtBarrier with { X = Barrier - 300 };

        Assert.Equal(0, TunnelShape.Echo([far], Barrier - 50, Barrier, pass: 0.4));
        Assert.Equal(0, TunnelShape.Echo([AtBarrier], Barrier + 50, Barrier, pass: 0.4));

        double full = TunnelShape.Echo([AtBarrier], Barrier - 1, Barrier, pass: 0);
        double part = TunnelShape.Echo([AtBarrier], Barrier - 1, Barrier, pass: 0.4);

        Assert.Equal(full * 0.6, part, 0.5);
    }

    [Fact]
    public void Wave_fades_in_under_the_title_and_out_at_the_right_edge()
    {
        Assert.Equal(0, TunnelShape.Fade(0, 1000, 0.3));
        Assert.True(TunnelShape.Fade(100, 1000, 0.3) < 0.5);
        Assert.Equal(1, TunnelShape.Fade(500, 1000, 0.3));
        Assert.Equal(0, TunnelShape.Fade(1000, 1000, 0.3));
    }

    [Fact]
    public void Packet_is_gone_once_it_and_its_echo_left_the_card()
    {
        Assert.False(TunnelShape.Gone(AtBarrier, Barrier, 1000));
        Assert.True(TunnelShape.Gone(AtBarrier with { X = 1400 }, Barrier, 1000));
    }
}
