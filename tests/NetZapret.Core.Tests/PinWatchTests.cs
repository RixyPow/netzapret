using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Чтение состояния автозамены для вкладки DNS (05.11): отметка, подозреваемые
/// и пины под присмотром по посредникам — на копиях во временной папке.
/// </summary>
public sealed class PinWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nz-pinwatch-" + Guid.NewGuid().ToString("N"));
    private readonly string _hosts;
    private readonly string _stamp;
    private readonly string _suspects;

    public PinWatchTests()
    {
        Directory.CreateDirectory(_dir);
        _hosts = Path.Combine(_dir, "hosts");
        _stamp = Path.Combine(_dir, "pin-refresh.txt");
        _suspects = Path.Combine(_dir, "pin-suspects.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Hosts(params string[] pins) =>
        File.WriteAllLines(_hosts,
        [
            "# hosts",
            "10.0.0.1 foreign.example # nz:via=comss",
            HostsEditor.BlockBegin,
            .. pins,
            HostsEditor.BlockEnd,
            "",
        ]);

    [Fact]
    public void Pins_are_counted_by_intermediary_in_trust_order_with_bare_last()
    {
        Hosts(
            "5.129.221.64 claude.ai # nz:via=zapret",
            "206.223.251.18 chatgpt.com # nz:via=comss",
            "206.223.251.18 openai.com # nz:via=comss",
            "1.2.3.4 grok.com",
            "9.9.9.9 old.example # nz:via=gone",
            "157.240.0.1 instagram.com # " + HostsEditor.DesyncMark);

        var watch = PinRefresh.Watch(_hosts, _stamp, _suspects);

        // Пин на адрес сервиса проверка не берёт — и под присмотром его нет;
        // незнакомый ключ — как без посредника; чужая строка вне блока не в счёт.
        Assert.Equal(
            [new PinWatchGroup("Comss DNS", 2), new PinWatchGroup("Zapret DNS", 1), new PinWatchGroup(null, 2)],
            watch.Groups);
        Assert.Equal(5, watch.Watched);
    }

    [Fact]
    public void Stamp_and_suspects_are_read_back()
    {
        Hosts();
        var at = new DateTimeOffset(2026, 11, 5, 10, 30, 0, TimeSpan.Zero);
        PinRefresh.Mark(at, _stamp);
        File.WriteAllText(_suspects, "[\"5.5.5.5\",\"1.1.1.1\"]");

        var watch = PinRefresh.Watch(_hosts, _stamp, _suspects);

        Assert.Equal(at, watch.Last);
        Assert.Equal(["1.1.1.1", "5.5.5.5"], watch.Suspects);
        Assert.Empty(watch.Groups);
    }

    [Fact]
    public void Missing_or_broken_files_read_as_never_checked()
    {
        Assert.Null(PinRefresh.LastChecked(_stamp));

        File.WriteAllText(_stamp, "вчера");
        Assert.Null(PinRefresh.LastChecked(_stamp));

        var watch = PinRefresh.Watch(_hosts, _stamp, _suspects);

        Assert.Null(watch.Last);
        Assert.Empty(watch.Suspects);
        Assert.Empty(watch.Groups);
    }
}
