using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Журнал движка как источник причины.
/// </summary>
/// <remarks>
/// Строки взяты из настоящего журнала, вместе с раскраской: движок красит
/// вывод и при записи в файл, и разбор без снятия управляющих
/// последовательностей не находил ничего.
/// </remarks>
public sealed class EngineLogTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private string Written(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-log-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, lines);
        _files.Add(path);

        return path;
    }

    /// <summary>Настоящая строка, как её пишет sing-box.</summary>
    private const string Real =
        "[31mERROR[0m[14973] [[38;5;187m749443243[0m 5.0s] " +
        "connection: open connection to web.whatsapp.com:443 using " +
        "outbound/hysteria2[\U0001F1FA\U0001F1F8 Hysteria2 | США (вход РФ)]: " +
        "timeout: no recent network activity";

    [Fact]
    public void TheOutboundAndTheErrorAreRecovered()
    {
        var log = Written(Real);

        var complaint = Assert.Single(EngineLog.Complaints(["whatsapp.com"], log));

        Assert.Equal("web.whatsapp.com", complaint.Host);
        Assert.Contains("США", complaint.Outbound);
        Assert.Equal("timeout: no recent network activity", complaint.Error);
    }

    /// <summary>
    /// Совпадение по зоне: проверяли whatsapp.com, жалоба про поддомен.
    /// </summary>
    /// <remarks>
    /// Приложение и браузер ходят на web.whatsapp.com и static.whatsapp.net,
    /// а проверка стучится в корень зоны. Требовать точного совпадения значило
    /// бы не найти ни одной жалобы ровно там, где они есть.
    /// </remarks>
    [Fact]
    public void ASubdomainComplaintAnswersForItsZone()
    {
        var log = Written(Real);

        Assert.Single(EngineLog.Complaints(["whatsapp.com"], log));
    }

    [Fact]
    public void AComplaintAboutTheZoneAnswersForASubdomain()
    {
        var log = Written(Real.Replace("web.whatsapp.com", "whatsapp.com"));

        Assert.Single(EngineLog.Complaints(["web.whatsapp.com"], log));
    }

    /// <summary>Берётся последняя жалоба: сервер могли сменить посреди прогона.</summary>
    [Fact]
    public void TheLatestComplaintWins()
    {
        var log = Written(
            Real,
            Real.Replace("США (вход РФ)", "Нидерланды").Replace("no recent network activity", "connection refused"));

        var complaint = Assert.Single(EngineLog.Complaints(["whatsapp.com"], log));

        Assert.Contains("Нидерланды", complaint.Outbound);
        Assert.Equal("timeout: connection refused", complaint.Error);
    }

    [Fact]
    public void NamesWeDidNotAskAboutAreIgnored()
    {
        var log = Written(Real);

        Assert.Empty(EngineLog.Complaints(["example.com"], log));
    }

    [Fact]
    public void SuccessfulLinesAreNotComplaints()
    {
        var log = Written(
            "[32mINFO[0m[14973] connection: outbound connection to whatsapp.com:443");

        Assert.Empty(EngineLog.Complaints(["whatsapp.com"], log));
    }

    [Fact]
    public void AMissingLogIsNotAnError()
    {
        Assert.Empty(EngineLog.Complaints(
            ["whatsapp.com"],
            Path.Combine(Path.GetTempPath(), $"netzapret-none-{Guid.NewGuid():N}.log")));
    }

    /// <summary>
    /// Журнал, который движок держит открытым, всё равно читается.
    /// </summary>
    /// <remarks>
    /// Без общего доступа чтение отказывало бы всегда — то есть ровно тогда,
    /// когда журнал и нужен: движок работает и пишет в него прямо сейчас.
    /// </remarks>
    [Fact]
    public void AnOpenLogIsStillReadable()
    {
        var path = Written(Real);

        using var held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        Assert.Single(EngineLog.Complaints(["whatsapp.com"], path));
    }
}
