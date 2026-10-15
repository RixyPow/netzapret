using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>Сторож уже исчерпал живые выходы: перечитать одну подписку, не меняя движки и их проверки.</summary>
public sealed class NoLiveSubscriptionRefresh
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(2);
    public sealed record State(DateTimeOffset NextAttemptAt, int Failures, int Cursor);
    public sealed record Result(bool Attempted, bool Downloaded, bool Changed);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, CancellationToken, Task<SubscriptionRead>> _read;
    private readonly Func<string, SubscriptionInfo?> _cached;

    public NoLiveSubscriptionRefresh(string statePath,
        Func<string, CancellationToken, Task<SubscriptionRead>>? read = null,
        Func<string, SubscriptionInfo?>? cached = null, Func<DateTimeOffset>? now = null)
    {
        _statePath = statePath; _now = now ?? (() => DateTimeOffset.UtcNow);
        _read = read ?? ((url, ct) => SubscriptionPool.ReadOneAsync(url, force: true, ct));
        _cached = cached ?? (url => SubscriptionPool.Kept(url)?.Info);
    }

    public async Task<Result> TryAsync(IReadOnlyList<string> activeUrls, CancellationToken ct)
    {
        // Это не второй таймер и не дополнительный перебор серверов. Повторный
        // сигнал отказа, пока панель читается, не создаёт второй запрос.
        if (!await _gate.WaitAsync(0, ct)) return new(false, false, false);
        try
        {
            var urls = activeUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToArray();
            if (urls.Length == 0) return new(false, false, false);
            var state = Load(); var now = _now();
            if (now < state.NextAttemptAt) return new(false, false, false);
            int index = (state.Cursor & int.MaxValue) % urls.Length;
            string url = urls[index];
            string before = Fingerprint(_cached(url));
            // Пишем ограничитель ДО запроса. Перезапуск приложения не должен
            // превращать недоступную панель в серию повторных обращений.
            var reserved = state with { NextAttemptAt = now + MinimumInterval, Cursor = (index + 1) % urls.Length };
            Save(reserved);
            SubscriptionRead fetched;
            try { fetched = await _read(url, ct); ct.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            { Save(Failed(reserved, _now())); return new(true, false, false); }
            bool ok = fetched.Source == SubscriptionReadSource.Panel && fetched.Info?.Servers.Count > 0;
            if (!ok) { Save(Failed(reserved, _now())); return new(true, false, false); }
            Save(reserved with { Failures = 0, NextAttemptAt = _now() + MinimumInterval });
            return new(true, true, before != Fingerprint(fetched.Info));
        }
        finally { _gate.Release(); }
    }

    private State Load()
    {
        if (!File.Exists(_statePath)) return new(DateTimeOffset.MinValue, 0, 0);
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath))
                ?? new(_now() + MinimumInterval, 0, 0); }
        catch (JsonException) { return new(_now() + MinimumInterval, 0, 0); }
    }
    private void Save(State state)
    {
        string full = Path.GetFullPath(_statePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(state)); File.Move(temp, full, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static State Failed(State state, DateTimeOffset now)
    {
        int failures = Math.Min(Math.Clamp(state.Failures, 0, 3) + 1, 3);
        double minutes = Math.Min(MaximumBackoff.TotalMinutes, MinimumInterval.TotalMinutes * Math.Pow(2, failures - 1));
        return state with { Failures = failures, NextAttemptAt = now.AddMinutes(minutes) };
    }
    private static string Fingerprint(SubscriptionInfo? info)
    {
        // Сравнение локальное, без журналирования ключей. В отличие от квоты
        // изменение адреса/ключа/SNI или тега требует применить новый список.
        string text = info is null ? string.Empty : string.Join("\n", info.Servers
            .Select(s => SubscriptionPool.Identity(s) + "|" + s.Tag).OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
