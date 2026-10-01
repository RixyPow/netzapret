using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Строка «Сервер» на «Главной» (владелец, 01.10): имя выхода без флага,
/// при автоподборе — «(авто)».
/// </summary>
public sealed class ServerLineTests
{
    private const string Italy = "\U0001F1EE\U0001F1F9 Италия";

    [Fact]
    public void AutoPickNamesTheLiveServerAndSaysAuto()
    {
        Assert.Equal("Италия (авто)", TunnelStatus.ServerLine(new AppSettings(), Italy));
    }

    [Fact]
    public void APinnedServerIsJustItsName()
    {
        Assert.Equal("Италия", TunnelStatus.ServerLine(new AppSettings { PreferredServer = Italy }, Italy));
    }

    /// <summary>WARP включён — выбран он, а сервер подписки на паузе не называется.</summary>
    [Fact]
    public void WarpIsNamedWhenOn()
    {
        var settings = new AppSettings { WarpEnabled = true, PreferredServer = Italy };

        Assert.Equal(Warp.MasqueTag, TunnelStatus.ServerLine(settings, Warp.MasqueTag));
        Assert.Equal(Warp.MasqueTag, TunnelStatus.ServerLine(settings, null));
    }

    /// <summary>Движок держит не выбранный — замена молчащему либо выбор не применён.</summary>
    [Fact]
    public void AnotherServerThanChosenIsTemporary()
    {
        Assert.Equal("Италия (временно)",
            TunnelStatus.ServerLine(new AppSettings { PreferredServer = "Эстония" }, Italy));
    }

    [Fact]
    public void StoppedEnginesShowTheChoice()
    {
        Assert.Equal("Автоподбор", TunnelStatus.ServerLine(new AppSettings(), null));
        Assert.Equal("Италия", TunnelStatus.ServerLine(new AppSettings { PreferredServer = Italy }, null));
    }
}
