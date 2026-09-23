using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Куда супервизор ставит группу выбора, когда движок поднялся.
/// </summary>
/// <remarks>
/// Движок берёт выбор из своего кэша, а не из конфига. 23.09 при «авто»
/// в настройках он стоял на Cloudflare WARP, и Instagram с WhatsApp шли
/// через него: прежняя проверка выбора молчала именно при «авто».
/// </remarks>
public sealed class SelectorAtStartTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto-latency")]
    public void AutoIsAssertedToo(string? preferred)
    {
        Assert.Equal(["auto-latency"], SingBoxService.StartExits(preferred));
    }

    /// <summary>
    /// Закреплённый выход мог уйти из подписки — тогда автоподбор,
    /// а не то, что движок помнил.
    /// </summary>
    [Fact]
    public void PinnedExitFallsBackToAuto()
    {
        Assert.Equal(["🇳🇱 NL", "auto-latency"], SingBoxService.StartExits("🇳🇱 NL"));
    }
}
