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
}
