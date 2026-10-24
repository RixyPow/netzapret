using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Причина выхода движка на старте — строкой FATAL, а не одним кодом (№29, 19.10).
/// </summary>
public sealed class EngineExitReasonTests
{
    [Fact]
    public void WithoutFatalLineOnlyTheCodeIsNamed() =>
        Assert.Equal("процесс завершился с кодом 1", SupervisedService.ExitReason(1, null));

    [Fact]
    public void FatalLineIsNamedWithoutLevelAndColours()
    {
        var line = "\u001b[31mFATAL\u001b[0m[0000] start service: start inbound/tun[tun-in]: "
            + "configure tun interface: Cannot create a file when that file already exists.";

        Assert.Equal(
            "процесс завершился с кодом 1: start service: start inbound/tun[tun-in]: "
            + "configure tun interface: Cannot create a file when that file already exists.",
            SupervisedService.ExitReason(1, line));
    }

    /// <summary>Снимок окна выкладывают в обсуждения — ключей сервера в нём быть не должно.</summary>
    [Fact]
    public void ServerKeysDoNotReachTheWindow()
    {
        var line = "FATAL[0000] initialize outbound[3]: \"uuid\": \"0b3c2f4e-1a2b-4c3d-9e8f-123456789abc\" is bad";

        var said = SupervisedService.ExitReason(1, line);

        Assert.DoesNotContain("0b3c2f4e", said);
        Assert.StartsWith("процесс завершился с кодом 1: initialize outbound[3]:", said);
    }

    [Fact]
    public void LongLineIsCut()
    {
        var said = SupervisedService.ExitReason(1, "FATAL[0000] " + new string('x', 1000));

        Assert.True(said.Length < 400);
        Assert.EndsWith("…", said);
    }

    /// <summary>
    /// winws2 FATAL не пишет: причину его отказа несёт строка «windivert: error …»
    /// (обсуждение №33, 22.10 — код 433 и больше ничего).
    /// </summary>
    [Fact]
    public void WinwsFailureLineIsTheReason()
    {
        const string line = "windivert: error opening filter: A device which does not exist was specified.";

        Assert.True(WinwsService.IsFailure(line));
        Assert.False(WinwsService.IsFailure("windivert initialized. capture is started."));
        Assert.False(WinwsService.IsFailure("Loading hostlist lists/error-pages.txt"));

        Assert.Equal(
            "процесс завершился с кодом 433: windivert: error opening filter: A device which does not exist was specified.",
            SupervisedService.ExitReason(433, line));
    }

    [Fact]
    public void Code433IsExplained()
    {
        Assert.Contains("WinDivert", NetZapret.Zapret.WinDivertDriver.Explain(433));
        Assert.Contains("GoodbyeDPI", NetZapret.Zapret.WinDivertDriver.Explain(433));
        Assert.Null(NetZapret.Zapret.WinDivertDriver.Explain(1));
    }

    /// <summary>
    /// Найденный рядом обход назван по имени и с процессом, а служба с автозапуском —
    /// отдельно (владелец 24.10; случай знакомого 22.10: goodbyedpi.exe и служба GoodbyeDPI).
    /// </summary>
    [Fact]
    public void Code433NamesTheBypassFoundNearby()
    {
        Diagnostics.OtherBypass[] nearby =
        [
            new("GoodbyeDPI", "процесс goodbyedpi.exe, №5176", Running: true),
            new("GoodbyeDPI", "служба «GoodbyeDPI»", Running: false),
        ];

        var text = NetZapret.Zapret.WinDivertDriver.Explain(433, nearby);

        Assert.StartsWith("драйвер перехвата WinDivert занят: рядом работает GoodbyeDPI (процесс goodbyedpi.exe, №5176)", text);
        Assert.Contains("служба «GoodbyeDPI»", text);
        Assert.DoesNotContain("Zapret)", text);

        // Только служба, процесса нет — драйвер держит не она: общее объяснение.
        Assert.Equal(NetZapret.Zapret.WinDivertDriver.Explain(433), NetZapret.Zapret.WinDivertDriver.Explain(433, [nearby[1]]));
        Assert.Null(NetZapret.Zapret.WinDivertDriver.Explain(1, nearby));
    }

    /// <summary>
    /// Выгрузка драйвера трогает только свой: под именем WinDivert свой драйвер
    /// держат GoodbyeDPI и другие обходы (22.10).
    /// </summary>
    [Theory]
    [InlineData(@"\??\C:\NetZapret\engines\zapret\exe\Monkey64.sys", true)]
    [InlineData(@"C:\NetZapret\engines\zapret\exe\Monkey64.sys", true)]
    [InlineData(@"\??\C:\GoodbyeDPI\x86_64\WinDivert64.sys", false)]
    [InlineData(@"\??\C:\Zapret\Dev\exe\Monkey64.sys", false)]
    [InlineData(@"System32\drivers\WinDivert.sys", false)]
    [InlineData(@"\??\C:\NetZapret\enginesX\Monkey64.sys", false)]
    [InlineData(null, false)]
    public void OnlyOurDriverIsOurs(string? imagePath, bool ours) =>
        Assert.Equal(ours, NetZapret.Zapret.WinDivertDriver.IsOurs(imagePath, @"C:\NetZapret\engines"));
}
