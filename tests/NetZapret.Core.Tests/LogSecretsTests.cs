using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ключи не ложатся на диск: журналы пишутся с «&lt;скрыто&gt;» вместо значения,
/// имя поля и строка остаются (решение владельца 05.11).
/// </summary>
public sealed class LogSecretsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nz-logsecrets-{Guid.NewGuid():N}");

    private static readonly string Key = new('c', 64);

    public LogSecretsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TheEngineLogNeverHoldsTheKey()
    {
        var path = Path.Combine(_dir, "sing-box.log");

        using (var log = new RollingLog(path))
        {
            log.AppendLine($"private_key={Key}");
            log.AppendLine("jc=4");
            log.AppendLine($"preshared_key={Key}");
            log.AppendLine("allowed_ip=0.0.0.0/0: IPC error -22");
        }

        var text = File.ReadAllText(path);

        Assert.DoesNotContain(Key, text);
        Assert.Contains("private_key=\"<скрыто>\"", text);
        Assert.Contains("preshared_key=\"<скрыто>\"", text);
        Assert.Contains("jc=4", text);
        Assert.Contains("allowed_ip=0.0.0.0/0: IPC error -22", text);
    }

    [Fact]
    public void TheWindowJournalNeverHoldsTheKeyEither()
    {
        var path = Path.Combine(_dir, "supervisor.log");

        using (var log = SharedLogWriter.TryOpen(path))
        {
            Assert.NotNull(log);
            log!.WriteLine($"[окно] header_protection_key = {Key}");
            log.WriteLine("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality#x");
        }

        var text = File.ReadAllText(path);

        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("vless://", text);
        Assert.Contains("[окно] header_protection_key = \"<скрыто>\"", text);
    }

    [Theory]
    [InlineData("winws2: capture is started")]
    [InlineData("INFO[0004] [3280520913 3ms] inbound/mixed[in]: inbound connection to 192.168.1.120:8917")]
    [InlineData("[супервизор] DNS (авто): напрямую — Comss.one отвечает")]
    public void OrdinaryLinesPassUntouched(string line) =>
        Assert.Equal(line, LogSecrets.Scrub(line));
}
