using System.Runtime.CompilerServices;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Стек подвисшего потока окна: снимок находит, где именно поток стоит.
/// </summary>
public sealed class UiStallStackTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WaitsForTheTunnel(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        entered.Set();
        release.Wait();
    }

    [Fact]
    public void A_stuck_thread_is_named_where_it_waits()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var stuck = new Thread(() => WaitsForTheTunnel(entered, release)) { IsBackground = true };
        stuck.Start();

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

            var line = UiStallStack.Snapshot(stuck.ManagedThreadId);

            Assert.StartsWith("стоит в: ", line);
            Assert.Contains("ManualResetEventSlim.Wait", line);
            Assert.Contains("UiStallStackTests.WaitsForTheTunnel", line);
        }
        finally
        {
            release.Set();
            stuck.Join(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("NetZapret.Gui.Views.VpnView+<RefreshAsync>d__45", "MoveNext", "Views.VpnView.RefreshAsync")]
    [InlineData("NetZapret.Gui.Views.VpnView+<>c__DisplayClass12_0", "<Show>b__0", "Views.VpnView.Show (лямбда)")]
    [InlineData("NetZapret.Proxy.ClashApi", "Get", "Proxy.ClashApi.Get")]
    [InlineData("System.Threading.ManualResetEventSlim", "Wait", "System.Threading.ManualResetEventSlim.Wait")]
    public void Frames_are_named_shortly(string type, string method, string expected) =>
        Assert.Equal(expected, UiStallStack.Name(type, method));

    [Fact]
    public void The_line_keeps_the_top_and_then_only_our_frames()
    {
        string[] frames =
        [
            "System.Threading.Monitor.Wait", "System.Threading.ManualResetEventSlim.Wait",
            "System.Threading.Tasks.Task.SpinThenBlockingWait", "System.Threading.Tasks.Task.Wait",
            "System.Threading.Tasks.Task.GetResultCore", "Views.VpnView.Refresh",
            "System.Windows.Threading.ExceptionWrapper.InternalRealCall", "Views.VpnView.OnTick",
        ];

        Assert.Equal(
            "System.Threading.Monitor.Wait ← System.Threading.ManualResetEventSlim.Wait ← "
            + "System.Threading.Tasks.Task.SpinThenBlockingWait ← System.Threading.Tasks.Task.Wait ← "
            + "Views.VpnView.Refresh ← Views.VpnView.OnTick",
            UiStallStack.Line(frames));
    }
}
