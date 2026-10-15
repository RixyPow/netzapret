using NetZapret.Core;
using Xunit;
namespace NetZapret.Core.Tests;
public sealed class RecoveryPolicyTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    [Theory]
    [InlineData(0, true)] [InlineData(299, true)] [InlineData(300, true)]
    [InlineData(301, false)] [InlineData(-1, false)]
    public void Threshold(double ms, bool good) => Assert.Equal(good, new RecoveryPolicy(new(), T).Acceptable(ms));
    [Fact] public void MissingIsBad() => Assert.False(new RecoveryPolicy(new(), T).Acceptable(null));
    [Fact] public void NonFiniteIsBad()
    { var p = new RecoveryPolicy(new(), T); Assert.False(p.Acceptable(double.NaN)); Assert.False(p.Acceptable(double.PositiveInfinity)); }
    [Fact] public void ThreeConsecutiveBadChecks()
    { var p = new RecoveryPolicy(new(), T); p.Observe(null); p.Observe(600); Assert.False(p.ShouldSweep(T)); p.Observe(null); Assert.True(p.ShouldSweep(T)); }
    [Fact] public void GoodResetsBadStreak()
    { var p = new RecoveryPolicy(new(), T); p.Observe(null); p.Observe(null); p.Observe(100); p.Observe(null); Assert.False(p.ShouldSweep(T)); }
    [Fact] public void SweepCooldown()
    { var p = new RecoveryPolicy(new() { BadChecks = 1 }, T); p.Observe(null); p.Swept(T); Assert.False(p.ShouldSweep(T.AddSeconds(59))); Assert.True(p.ShouldSweep(T.AddSeconds(60))); }
    [Fact] public void RefreshMinimumInterval()
    { var p = new RecoveryPolicy(new(), T); p.RefreshStarted(T); Assert.False(p.CanRefresh(T.AddSeconds(299))); Assert.True(p.CanRefresh(T.AddSeconds(300))); }
    [Fact] public void BackoffDoublesAndCaps()
    { var p = new RecoveryPolicy(new(), T); var now = T; foreach (var seconds in new[] { 300,600,1200,1800,1800 }) { p.RefreshStarted(now); p.RefreshFinished(now,false); Assert.False(p.CanRefresh(now.AddSeconds(seconds-1))); Assert.True(p.CanRefresh(now.AddSeconds(seconds))); now = now.AddSeconds(seconds); } }
    [Fact] public void SuccessResetsRefreshBackoff()
    { var p = new RecoveryPolicy(new(), T); p.RefreshFinished(T,false); p.RefreshFinished(T.AddMinutes(5),false); p.RefreshFinished(T.AddMinutes(15),true); p.RefreshFinished(T.AddMinutes(20),false); Assert.True(p.CanRefresh(T.AddMinutes(25))); }
    [Fact] public void HealthyExitDoesNotResetFailedPanelBackoff()
    { var p = new RecoveryPolicy(new(), T); p.RefreshFinished(T,false); p.Observe(100); p.RefreshFinished(T.AddMinutes(5),false); Assert.False(p.CanRefresh(T.AddMinutes(14))); Assert.True(p.CanRefresh(T.AddMinutes(15))); }
    [Fact] public void PeriodicIsNotCacheTtl()
    { var p = new RecoveryPolicy(new(), T); Assert.False(p.PeriodicDue(T.AddMinutes(30))); Assert.True(p.PeriodicDue(T.AddMinutes(360))); }
    [Fact] public void PeriodicCanBeDisabled()
    { var p = new RecoveryPolicy(new() { AutoRefreshMinutes = 0 }, T); Assert.False(p.PeriodicDue(T.AddYears(10))); }
    [Fact] public void SuccessfulPeriodicRefreshReschedules()
    { var p = new RecoveryPolicy(new() { AutoRefreshMinutes = 60 }, T); p.RefreshFinished(T.AddHours(1),true); Assert.False(p.PeriodicDue(T.AddMinutes(119))); Assert.True(p.PeriodicDue(T.AddHours(2))); }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(60001)]
    public void InvalidThresholdRejected(double ms) => Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryPolicy(new() { MaxLatencyMs = ms }, T));
    [Fact] public void InvalidBackoffRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryPolicy(new() { MaxRefreshBackoffSeconds = 100 }, T));
    [Fact] public void DefaultThresholdIs300() => Assert.Equal(300, new RecoveryOptions().MaxLatencyMs);
    [Fact] public void CustomThresholdIsUsed()
    { var p = new RecoveryPolicy(new() { MaxLatencyMs = 700 }, T); Assert.True(p.Acceptable(700)); Assert.False(p.Acceptable(701)); }
    [Fact] public void DisabledByDefault() => Assert.False(new RecoveryOptions().Enabled);
}
