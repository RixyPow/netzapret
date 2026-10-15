namespace NetZapret.Core;

/// <summary>Политику повторов проверяем без сети и процессов, чтобы тесты не зависели от провайдера.</summary>
public sealed record RecoveryOptions
{
    public bool Enabled { get; init; }
    public double MaxLatencyMs { get; init; } = 300;
    public int BadChecks { get; init; } = 3;
    public int CheckSeconds { get; init; } = 30;
    public int ProbeTimeoutSeconds { get; init; } = 3;
    public int CandidatesPerRound { get; init; } = 12;
    public int SweepRetrySeconds { get; init; } = 60;
    public int MinRefreshSeconds { get; init; } = 300;
    public int MaxRefreshBackoffSeconds { get; init; } = 1800;
    public int AutoRefreshMinutes { get; init; } = 360;
    public bool AllowPinnedReplacement { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(MaxLatencyMs) || MaxLatencyMs < 1 || MaxLatencyMs > 60000
            || BadChecks is < 1 or > 20 || CheckSeconds is < 5 or > 3600
            || ProbeTimeoutSeconds is < 1 or > 30 || CandidatesPerRound is < 1 or > 50
            || SweepRetrySeconds is < 10 or > 3600 || MinRefreshSeconds is < 60 or > 86400
            || MaxRefreshBackoffSeconds < MinRefreshSeconds || MaxRefreshBackoffSeconds > 86400
            || AutoRefreshMinutes is < 0 or > 10080)
            throw new ArgumentOutOfRangeException(nameof(RecoveryOptions));
    }
}

public sealed class RecoveryPolicy
{
    private readonly RecoveryOptions _options;
    private int _bad;
    private int _refreshFailures;
    private DateTimeOffset _sweepAt = DateTimeOffset.MinValue;
    private DateTimeOffset _refreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset _periodicAt;
    public RecoveryPolicy(RecoveryOptions options, DateTimeOffset now)
    {
        options.Validate(); _options = options;
        _periodicAt = options.AutoRefreshMinutes == 0 ? DateTimeOffset.MaxValue
            : now.AddMinutes(options.AutoRefreshMinutes);
    }
    public bool Acceptable(double? ms) => ms is >= 0 && double.IsFinite(ms.Value)
        && ms.Value <= _options.MaxLatencyMs;
    public bool Observe(double? ms)
    {
        if (Acceptable(ms)) { _bad = 0; return true; }
        _bad = Math.Min(_options.BadChecks, _bad + 1); return false;
    }
    public bool ShouldSweep(DateTimeOffset now) => _bad >= _options.BadChecks && now >= _sweepAt;
    public void Swept(DateTimeOffset now) => _sweepAt = now.AddSeconds(_options.SweepRetrySeconds);
    public bool CanRefresh(DateTimeOffset now) => now >= _refreshAt;
    public bool PeriodicDue(DateTimeOffset now) => now >= _periodicAt && CanRefresh(now);
    public void RefreshStarted(DateTimeOffset now) => _refreshAt = now.AddSeconds(_options.MinRefreshSeconds);
    public void RefreshFinished(DateTimeOffset now, bool success)
    {
        if (success)
        {
            _refreshFailures = 0;
            _refreshAt = now.AddSeconds(_options.MinRefreshSeconds);
            _periodicAt = _options.AutoRefreshMinutes == 0 ? DateTimeOffset.MaxValue
                : now.AddMinutes(_options.AutoRefreshMinutes);
        }
        else
        {
            _refreshFailures = Math.Min(_refreshFailures + 1, 20);
            double seconds = Math.Min(_options.MaxRefreshBackoffSeconds,
                _options.MinRefreshSeconds * Math.Pow(2, _refreshFailures - 1));
            _refreshAt = now.AddSeconds(seconds);
            _periodicAt = _refreshAt;
        }
    }
}
