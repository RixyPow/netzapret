using System.Text.Json;
using NetZapret.Core;
using NetZapret.Subscriptions;
using Xunit;
namespace NetZapret.Core.Tests;
public sealed class NoLiveSubscriptionRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nz-refresh-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "state.json");
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private static SubscriptionInfo Info(string host = "new.example") => new() { Servers = [new()
        { Protocol = ProxyProtocol.Hysteria2, Host = host, Tag = "test", Port = 443, Credential = "TEST", Transport = "udp", Security = "tls" }] };
    private static SubscriptionRead Read(SubscriptionInfo? info, SubscriptionReadSource source = SubscriptionReadSource.Panel) => new(info, null, source, null);
    [Fact] public void DisabledByDefault() => Assert.False(new AppSettings().RefreshSubscriptionWhenNoLiveExits);
    [Fact] public async Task OneActiveSourcePerSignalAndThirtyMinuteMinimum()
    {
        var seen = new List<string>();
        var r = new NoLiveSubscriptionRefresh(StatePath, (url, _) => { seen.Add(url); return Task.FromResult(Read(Info())); }, _ => Info(), () => _now);
        Assert.True((await r.TryAsync(["a", "b", "c"], default)).Attempted);
        Assert.False((await r.TryAsync(["a", "b", "c"], default)).Attempted);
        _now = _now.AddMinutes(29); Assert.False((await r.TryAsync(["a", "b", "c"], default)).Attempted);
        _now = _now.AddMinutes(1); await r.TryAsync(["a", "b", "c"], default);
        Assert.Equal(new[] { "a", "b" }, seen);
    }
    [Fact] public async Task CooldownSurvivesObjectRecreation()
    {
        int reads = 0;
        NoLiveSubscriptionRefresh Create() => new(StatePath, (_, _) => { reads++; return Task.FromResult(Read(Info())); }, _ => Info(), () => _now);
        await Create().TryAsync(["a"], default); await Create().TryAsync(["a"], default);
        Assert.Equal(1, reads);
    }
    [Fact] public async Task FailedPanelBackoffThirtySixtyOneTwenty()
    {
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, _) => Task.FromResult(Read(Info(), SubscriptionReadSource.Reserve)), _ => Info(), () => _now);
        foreach (int minutes in new[] {30,60,120,120})
        {
            var result = await r.TryAsync(["a"], default); Assert.True(result.Attempted); Assert.False(result.Downloaded);
            var state = JsonSerializer.Deserialize<NoLiveSubscriptionRefresh.State>(File.ReadAllText(StatePath))!;
            Assert.Equal(_now.AddMinutes(minutes), state.NextAttemptAt); _now = state.NextAttemptAt;
        }
    }
    [Fact] public async Task UnchangedPanelDoesNotRequestApply()
    {
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, _) => Task.FromResult(Read(Info())), _ => Info(), () => _now);
        var result = await r.TryAsync(["a"], default); Assert.True(result.Downloaded); Assert.False(result.Changed);
    }
    [Fact] public async Task ChangedPanelIsReportedWithoutRestartingAnything()
    {
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, _) => Task.FromResult(Read(Info("new.example"))), _ => Info("old.example"), () => _now);
        Assert.True((await r.TryAsync(["a"], default)).Changed);
    }
    [Fact] public async Task ReserveIsNotSuccessfulDownload()
    {
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, _) => Task.FromResult(Read(Info(), SubscriptionReadSource.Reserve)), _ => Info(), () => _now);
        var result = await r.TryAsync(["a"], default); Assert.False(result.Downloaded); Assert.False(result.Changed);
    }
    [Fact] public async Task EmptyPoolAndKeysDoNotFetchAnything()
    {
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, _) => throw new Exception("must not fetch"), _ => Info(), () => _now);
        Assert.False((await r.TryAsync([], default)).Attempted); Assert.False(File.Exists(StatePath));
    }
    [Fact] public async Task ParallelSignalsDoNotMultiplyRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var r = new NoLiveSubscriptionRefresh(StatePath, async (_, _) => { reads++; started.SetResult(); await finish.Task; return Read(Info()); }, _ => Info(), () => _now);
        var first = r.TryAsync(["a"], default); await started.Task;
        Assert.False((await r.TryAsync(["a"], default)).Attempted); finish.SetResult(); await first;
        Assert.Equal(1, reads);
    }
    [Fact] public async Task CancelledDownloadPropagatesAndKeepsReservation()
    {
        using var cts = new CancellationTokenSource();
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Read(Info())); }, _ => Info(), () => _now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.TryAsync(["a"], cts.Token));
        Assert.True(File.Exists(StatePath));
    }
    [Fact] public async Task StateContainsNoSubscriptionSecrets()
    {
        var r = new NoLiveSubscriptionRefresh(StatePath, (_, _) => Task.FromResult(Read(Info())), _ => Info(), () => _now);
        await r.TryAsync(["https://provider.invalid/sub/PRIVATE_TOKEN"], default);
        string text = File.ReadAllText(StatePath); Assert.DoesNotContain("PRIVATE_TOKEN", text); Assert.DoesNotContain("provider", text);
    }
}
