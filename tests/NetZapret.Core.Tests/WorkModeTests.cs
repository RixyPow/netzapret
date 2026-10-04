using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Три режима вместо двух выключателей (владелец, 04.10): «Десинк», «Туннель», «Гибрид».
/// </summary>
public sealed class WorkModeTests
{
    [Theory]
    [InlineData(WorkMode.Desync, true, false, OperatingMode.DesyncOnly)]
    [InlineData(WorkMode.Tunnel, false, true, OperatingMode.ProxyAll)]
    [InlineData(WorkMode.Route, true, true, OperatingMode.Selective)]
    public void Each_mode_sets_the_switches_it_means(WorkMode mode, bool desync, bool tunnel, OperatingMode legacy)
    {
        var choice = WorkModes.Apply(new EngineChoice { Desync = false, Tunnel = false }, mode);

        Assert.Equal(desync, choice.Desync);
        Assert.Equal(tunnel, choice.Tunnel);
        Assert.Equal(legacy, choice.Mode);
        Assert.Equal(mode, WorkModes.Of(choice));
    }

    /// <summary>Режим — из нынешних выключателей, без переноса настроек.</summary>
    [Fact]
    public void Nothing_running_is_no_mode()
    {
        Assert.Null(WorkModes.Of(new EngineChoice { Desync = false, Tunnel = false }));
    }

    /// <summary>«Без исключений» — настройка внутри «Туннеля», смена режима её не трогает.</summary>
    [Fact]
    public void Switching_modes_keeps_the_no_exceptions_setting()
    {
        var strict = new EngineChoice { Desync = false, Tunnel = true, IgnoreExclusions = true };

        Assert.True(WorkModes.Apply(strict, WorkMode.Route).IgnoreExclusions);
        Assert.Equal(OperatingMode.ProxyStrict, WorkModes.Apply(strict, WorkMode.Tunnel).Mode);
    }

    [Fact]
    public void Every_mode_has_a_name_and_an_explanation()
    {
        foreach (var mode in WorkModes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(WorkModes.Name(mode)));
            Assert.False(string.IsNullOrWhiteSpace(WorkModes.Explain(mode)));
        }

        Assert.Equal("Гибрид", WorkModes.Name(WorkMode.Route));
    }

    /// <summary>В трее выбор сразу поднимает движки — «Туннелю» без выхода поднимать нечего.</summary>
    [Fact]
    public void Tunnel_needs_an_exit_to_be_chosen()
    {
        var noExit = AppSettings.Fresh with { SubscriptionUrl = null, WarpEnabled = false, KeysEnabled = false };
        var withExit = noExit with { SubscriptionUrl = "https://example.invalid/sub" };

        Assert.False(WorkModes.CanChoose(noExit, WorkMode.Tunnel));
        Assert.True(WorkModes.CanChoose(noExit, WorkMode.Desync));
        Assert.True(WorkModes.CanChoose(noExit, WorkMode.Route));
        Assert.True(WorkModes.CanChoose(withExit, WorkMode.Tunnel));
    }

    [Fact]
    public void Choosing_a_mode_rewrites_only_the_switches()
    {
        var settings = AppSettings.Fresh.With(new EngineChoice { Desync = true, Tunnel = false }) with { PresetName = "X" };

        var next = WorkModes.Choose(settings, WorkMode.Tunnel);

        Assert.Equal(WorkMode.Tunnel, WorkModes.Of(next.Engines));
        Assert.Equal("X", next.PresetName);
    }
}
