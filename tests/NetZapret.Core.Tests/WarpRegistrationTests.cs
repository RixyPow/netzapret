using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Неудачная регистрация WARP видна по концу журнала движка (совет в карточке, 08.11).</summary>
public sealed class WarpRegistrationTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"nz-warp-{Guid.NewGuid():N}.log");

    public void Dispose() => File.Delete(_log);

    [Fact]
    public void Fresh_not_initialized_lines_mean_the_account_is_not_made()
    {
        File.WriteAllText(_log, "ERROR connection: open connection to x using outbound/masque[Cloudflare WARP]: tunnel not initialized\n");

        Assert.True(Warp.RegistrationFailing(_log));
    }

    [Fact]
    public void Old_failure_far_above_the_tail_is_not_counted()
    {
        File.WriteAllText(_log, "tunnel not initialized\n" + new string('x', 70 * 1024) + "\nINFO ok\n");

        Assert.False(Warp.RegistrationFailing(_log));
    }

    [Fact]
    public void Missing_log_is_not_a_failure()
    {
        Assert.False(Warp.RegistrationFailing(_log + ".нет"));
    }
}
