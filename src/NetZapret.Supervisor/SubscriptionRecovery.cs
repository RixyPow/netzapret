using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Supervisor;

/// <summary>Работает внутри последовательной проверки супервизора: второй сетевой таймер не конкурирует с надзором.</summary>
public sealed class SubscriptionRecovery
{
    private const string Selector = "auto", Latency = "auto-latency";
    private readonly SingBoxService _service;
    private readonly string _config, _executable;
    private readonly RecoveryOptions _options;
    private readonly RecoveryPolicy _policy;
    private readonly Func<CancellationToken, Task<bool>> _restartWinws;
    private readonly Func<AppSettings, CancellationToken, Task<bool>> _buildConfig;
    private readonly Func<IReadOnlyList<string>> _subscriptionUrls;
    private string? _currentTag;
    private double? _currentMs;
    private DateTimeOffset _checkAt;
    private bool _lastGood = true;
    private int _cursor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SubscriptionRecovery(SingBoxService service, string config, string executable,
        RecoveryOptions options, Func<CancellationToken, Task<bool>> restartWinws,
        Func<AppSettings, CancellationToken, Task<bool>> buildConfig,
        Func<IReadOnlyList<string>> subscriptionUrls)
    {
        _service = service; _config = Path.GetFullPath(config); _executable = executable;
        _options = options; _policy = new(options, DateTimeOffset.UtcNow); _restartWinws = restartWinws; _buildConfig = buildConfig; _subscriptionUrls = subscriptionUrls;
    }

    public static void Attach(SingBoxService service, string config, string executable,
        Func<CancellationToken, Task<bool>> restartWinws,
        Func<AppSettings, CancellationToken, Task<bool>> buildConfig,
        Func<IReadOnlyList<string>> subscriptionUrls)
    {
        const string path = "config/subscription-recovery.json";
        if (!File.Exists(path)) return;
        try
        {
            var options = RecoverySettingsFile.Load(path);
            if (!options.Enabled) return;
            var settings = AppSettings.Load();
            if (settings.WarpEnabled || (!options.AllowPinnedReplacement
                && !string.IsNullOrWhiteSpace(settings.PreferredServer)))
            {
                Log("disabled: WARP or pinned server (AllowPinnedReplacement=false)"); return;
            }
            var recovery = new SubscriptionRecovery(service, config, executable, options, restartWinws, buildConfig, subscriptionUrls);
            service.RecoveryCheck = recovery.CheckAsync;
            Log("enabled: threshold=" + options.MaxLatencyMs + "ms; no automatic direct fallback");
        }
        catch (Exception ex) { Log("disabled: invalid configuration (" + ex.GetType().Name + ")"); }
    }

    private static void Log(string text) => Console.WriteLine("[subscription-recovery] " + text);
    private ClashApi Api() => new(keys: EngineKeys.Current(_config));
    private ServiceCheck Result => _lastGood ? ServiceCheck.Healthy : ServiceCheck.UpstreamDown;

    private async Task<ServiceCheck> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _checkAt) return Result;
            _checkAt = now.AddSeconds(_options.CheckSeconds);
            var settings = AppSettings.Load();
            // Изменения настроек не должны молча подменять закреплённый пользователем выход.
            if (settings.WarpEnabled || (! _options.AllowPinnedReplacement
                && !string.IsNullOrWhiteSpace(settings.PreferredServer))) return ServiceCheck.Healthy;
            using var api = Api();
            if (!await api.AliveAsync(ct)) return ServiceCheck.Broken;
            ct.ThrowIfCancellationRequested();
            var selected = await api.SelectedAsync(Selector, ct);
            if (selected == Latency) selected = await api.SelectedAsync(Latency, ct);
            double? delay = selected is null || selected == "direct" ? null : await MeasureAsync(api, selected, settings, ct);
            _currentTag = selected; _currentMs = delay;
            _lastGood = _policy.Observe(delay);
            bool search = _policy.ShouldSweep(now);
            if (search)
            {
                _policy.Swept(now);
                Log("unacceptable exit confirmed; probing candidates");
                _lastGood = await SearchAsync(api, settings, ct);
            }
            bool periodic = _policy.PeriodicDue(now);
            if (periodic || (search && !_lastGood && _policy.CanRefresh(now)))
            {
                _policy.RefreshStarted(now);
                bool success = await RefreshAsync(settings, ct);
                _policy.RefreshFinished(DateTimeOffset.UtcNow, success);
                if (success && !_lastGood)
                {
                    using var nextApi = Api();
                    _lastGood = await SearchAsync(nextApi, settings, ct);
                    // Неизменившаяся корректная подписка — не ошибка загрузки и не повод удваивать паузу.
                }
            }
            return Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log("recovery failed (" + ex.GetType().Name + "); keeping last configuration");
            _lastGood = false; return ServiceCheck.UpstreamDown;
        }
        finally { _gate.Release(); }
    }

    private async Task<double?> MeasureAsync(ClashApi api, string tag, AppSettings settings, CancellationToken ct)
    {
        var result = await api.MeasureAsync(tag, Ping.UrlOf(settings),
            TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds), ct);
        ct.ThrowIfCancellationRequested(); // ClashApi может вернуть null при отмене; остановка не должна считаться отказом сервера.
        double? ms = result?.TotalMilliseconds;
        var health = ServerHealthCache.Load();
        health.Set(new ServerHealth { Tag = tag, Success = result is not null,
            LatencyMs = ms, CheckedAt = DateTimeOffset.Now });
        health.Save();
        return ms;
    }

    private async Task<bool> SearchAsync(ClashApi api, AppSettings settings, CancellationToken ct)
    {
        var members = await api.MembersAsync(Selector, ct);
        ct.ThrowIfCancellationRequested();
        if (members is null) return false;
        // Исключения пользователя сохраняются, а группы и прямой выход кандидатами не становятся.
        var remote = members.Where(t => t != Selector && t != Latency && t != "direct" && t != "block"
            && !settings.AutoPickExcluded.Contains(t)).Distinct(StringComparer.Ordinal).ToArray();
        if (settings.ForeignExitsOnly)
            remote = remote.Where(t => !SingBoxConfigCompiler.IsDomesticExitTag(t)).ToArray();
        if (remote.Length == 0) return false;
        var scored = new List<(string Tag, double Ms)>();
        int count = Math.Min(remote.Length, _options.CandidatesPerRound);
        int offset = _cursor % remote.Length;
        for (int i = 0; i < count; i++)
        {
            string tag = remote[(offset + i) % remote.Length];
            var ms = await MeasureAsync(api, tag, settings, ct);
            if (ms is >= 0) scored.Add((tag, ms.Value));
        }
        _cursor = (offset + count) % remote.Length; // Иначе серверы за первой порцией никогда не проверялись бы.
        var best = scored.OrderBy(s => s.Ms).FirstOrDefault();
        if (best.Tag is null) { Log("candidate batch: none answered"); return false; }
        // Один случайный ответ не доказывает устойчивость: выбранного кандидата проверяем повторно.
        var confirm = await MeasureAsync(api, best.Tag, settings, ct);
        if (confirm is null) return false;
        if (!_policy.Acceptable(confirm) && _currentMs is { } current
            && current <= confirm.Value && _currentTag is not null)
        { Log("keeping current slow exit; candidate is not better"); return false; }
        if (!await api.SelectAsync(Selector, best.Tag, ct)) return false;
        ct.ThrowIfCancellationRequested();
        Log("selected candidate: " + Math.Round(confirm.Value) + "ms"
            + (_policy.Acceptable(confirm) ? " (acceptable)" : " (slow temporary exit)"));
        return _policy.Acceptable(confirm);
    }

    private async Task<bool> RefreshAsync(AppSettings settings, CancellationToken ct)
    {
        var sources = _subscriptionUrls().ToList();
        if (sources.Count == 0 && !string.IsNullOrWhiteSpace(settings.SubscriptionUrl)) sources.Add(settings.SubscriptionUrl);
        sources = sources.Distinct(StringComparer.Ordinal).ToList();
        if (sources.Count == 0) { Log("no subscription URLs; only server rechecks available"); return true; }
        bool received = false;
        foreach (var url in sources)
        {
            var read = await SubscriptionPool.ReadOneAsync(url, force: true, ct);
            ct.ThrowIfCancellationRequested();
            received |= read.Source == SubscriptionReadSource.Panel && read.Info?.Servers.Count > 0;
        }
        if (!received) { Log("panels unavailable; old list retained; refresh backoff active"); return false; }
        var temp = _config + ".recovery-candidate.json";
        try
        {
            bool built = await _buildConfig(settings with { ProxyConfigPath = temp }, ct);
            if (!built || !File.Exists(temp)) { Log("candidate compilation failed; old config retained"); return false; }
            if (Fingerprint(File.ReadAllText(_config)) == Fingerprint(File.ReadAllText(temp)))
            { Log("subscription fetched; endpoints unchanged; no restart"); return true; }
            if (!await ValidateAsync(temp, ct)) { Log("sing-box check rejected candidate; no restart"); return false; }
            return await ApplyAsync(temp, ct);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    // Новые API-ключи и временные пути не требуют перезапуска; адреса, маршруты и DNS учитываются.
    internal static string Fingerprint(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["outbounds"] is JsonArray exits)
        {
            root["outbounds"] = new JsonArray(exits.OfType<JsonObject>()
                .Where(n => n["type"]?.GetValue<string>() is not ("selector" or "urltest"))
                .OrderBy(n => n["tag"]?.GetValue<string>(), StringComparer.Ordinal)
                .Select(n => n.DeepClone()).ToArray());
        }
        if (root["endpoints"] is JsonArray endpoints)
            root["endpoints"] = new JsonArray(endpoints.OfType<JsonObject>()
                .OrderBy(n => n["tag"]?.GetValue<string>(), StringComparer.Ordinal)
                .Select(n => n.DeepClone()).ToArray());
        return string.Join("\n", new[] { "outbounds", "endpoints", "route", "dns" }
            .Select(k => root[k]?.ToJsonString() ?? "null"));
    }

    private async Task<bool> ValidateAsync(string config, CancellationToken ct)
    {
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("check"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
        using var process = Process.Start(start);
        if (process is null) return false;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr); // Текст конфигурации и ошибок может содержать ключи; в журнал его не записываем.
            return process.ExitCode == 0;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private async Task<bool> ApplyAsync(string candidate, CancellationToken ct)
    {
        var old = File.ReadAllBytes(_config);
        var oldFile = _config + ".recovery-previous.json";
        File.WriteAllBytes(oldFile, old); // Файл содержит ключи, поэтому runtime/ нельзя публиковать.
        Log("configuration changed: controlled engine restart; active connections may break");
        await _service.StopAsync(ct);
        try
        {
            File.Move(candidate, _config, overwrite: true);
            if (!await _restartWinws(ct)) throw new InvalidOperationException("winws restart failed");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            if (!await _service.StartAsync(TimeSpan.FromSeconds(25), ct))
                throw new InvalidOperationException("sing-box restart failed");
            _cursor = 0; _currentTag = null; _currentMs = null;
            Log("new configuration running"); return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { File.WriteAllBytes(_config, old); throw; }
        catch (Exception ex)
        {
            Log("apply failed (" + ex.GetType().Name + "); rolling back");
            await _service.StopAsync(ct);
            File.WriteAllBytes(_config, old);
            bool winws = await _restartWinws(ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            bool restored = await _service.StartAsync(TimeSpan.FromSeconds(25), ct);
            Log(winws && restored ? "previous configuration restored" : "rollback incomplete; supervisor will retry");
            return false;
        }
    }
}
