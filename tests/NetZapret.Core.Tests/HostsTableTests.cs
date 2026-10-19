using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Файл hosts таблицей и выключение записей — раздел «Файл hosts» с 19.10.
/// </summary>
public sealed class HostsTableTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"netzapret-hosts-table-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(
            Path.GetDirectoryName(_file)!, Path.GetFileNameWithoutExtension(_file) + "*"))
        {
            try { File.Delete(f); } catch (Exception) { }
        }
    }

    // Как у владельца 19.10: блок Zapret GUI внутри нашего, раздел Telegram без закрытия.
    private const string Sample = """
        # >>> netzapret begin >>>
        # Записи ведёт NetZapret.
        83.217.211.149 claude.ai
        157.240.245.174 instagram.com # nz:desync
        # 83.217.211.149 off.example
        # >>> zapretgui:hosts managed begin >>>
        83.217.211.149 claude.ai
        83.217.211.150 a.claude.ai
        # <<< zapretgui:hosts managed end <<<
        # <<< netzapret end <<<
        0.0.0.0 ads.example
        # --- Telegram Proxy (auto-managed by Zapret 2 GUI) ---
        149.154.167.220 t.me api.telegram.org

        72.56.93.144 canva.com
        """;

    private IReadOnlyList<HostsRow> Rows()
    {
        File.WriteAllText(_file, Sample);
        return HostsTable.Read(_file);
    }

    [Fact]
    public void The_source_is_the_innermost_block()
    {
        var rows = Rows();

        Assert.Equal(HostsTable.OurSource, rows.First(r => r.Name == "claude.ai").Source);
        Assert.True(rows.First(r => r.Name == "claude.ai").Ours);

        var nested = rows.Single(r => r.Name == "a.claude.ai");
        Assert.Equal("Zapret GUI", nested.Source);
        Assert.False(nested.Ours);

        Assert.Equal("Zapret GUI", rows.Single(r => r.Name == "t.me").Source);
        Assert.Null(rows.Single(r => r.Name == "canva.com").Source);
        Assert.Null(rows.Single(r => r.Name == "ads.example").Source);
    }

    [Fact]
    public void The_kind_follows_the_mark_and_the_stub()
    {
        var rows = Rows();

        Assert.Equal(HostsPinKind.Service, rows.Single(r => r.Name == "instagram.com").Kind);
        Assert.Equal(HostsPinKind.Proxy, rows.First(r => r.Name == "claude.ai").Kind);
        Assert.Equal(HostsPinKind.Stub, rows.Single(r => r.Name == "ads.example").Kind);
        Assert.Equal(HostsPinKind.Proxy, rows.Single(r => r.Name == "canva.com").Kind);
    }

    [Fact]
    public void Only_the_later_enabled_line_is_a_repeat()
    {
        var claude = Rows().Where(r => r.Name == "claude.ai").ToList();

        Assert.Equal(2, claude.Count);
        Assert.False(claude[0].Repeat);
        Assert.True(claude[1].Repeat);
    }

    [Fact]
    public void A_disabled_record_is_a_row_and_not_a_comment()
    {
        var rows = Rows();

        Assert.False(rows.Single(r => r.Name == "off.example").Enabled);
        Assert.DoesNotContain(rows, r => r.Name.Contains("netzapret") || r.Name.Contains("NetZapret"));
    }

    [Fact]
    public void Disabling_one_name_splits_the_line()
    {
        var tme = Rows().Single(r => r.Name == "t.me");

        var (_, changed) = HostsEditor.SetEnabled([(tme.Line, "t.me")], enabled: false, _file);

        Assert.Equal(1, changed);

        var after = HostsTable.Read(_file);
        Assert.True(after.Single(r => r.Name == "api.telegram.org").Enabled);
        Assert.False(after.Single(r => r.Name == "t.me").Enabled);

        // И обратно — строкой, а не исчезновением.
        var off = after.Single(r => r.Name == "t.me");
        HostsEditor.SetEnabled([(off.Line, "t.me")], enabled: true, _file);

        Assert.True(HostsTable.Read(_file).Single(r => r.Name == "t.me").Enabled);
    }

    [Fact]
    public void The_note_survives_toggling()
    {
        var instagram = Rows().Single(r => r.Name == "instagram.com");

        HostsEditor.SetEnabled([(instagram.Line, "instagram.com")], enabled: false, _file);

        var off = HostsTable.Read(_file).Single(r => r.Name == "instagram.com");
        Assert.False(off.Enabled);
        Assert.Contains(HostsEditor.DesyncMark, off.Note);

        // Выключенный пин щит не видит: Windows его не читает.
        Assert.DoesNotContain("instagram.com", HostsEditor.DesyncPins(_file));
    }

    [Fact]
    public void Pinning_keeps_disabled_lines_of_our_block()
    {
        Rows();

        HostsEditor.Pin(new Dictionary<string, string> { ["new.example"] = "1.2.3.4" }, _file);

        var after = HostsTable.Read(_file);
        var off = after.Single(r => r.Name == "off.example");

        Assert.False(off.Enabled);
        Assert.Equal(HostsTable.OurSource, off.Source);
    }

    [Fact]
    public void Pinning_a_disabled_name_again_replaces_it()
    {
        Rows();

        HostsEditor.Pin(new Dictionary<string, string> { ["off.example"] = "1.2.3.4" }, _file);

        var off = Assert.Single(HostsTable.Read(_file), r => r.Name == "off.example");
        Assert.True(off.Enabled);
        Assert.Equal("1.2.3.4", off.Address);
    }

    [Fact]
    public void A_block_of_disabled_lines_stays_after_removal()
    {
        File.WriteAllText(_file, """
            # >>> netzapret begin >>>
            1.1.1.1 on.example
            # 2.2.2.2 off.example
            # <<< netzapret end <<<
            """);

        var on = HostsTable.Read(_file).Single(r => r.Name == "on.example");
        HostsEditor.RemoveNames([(on.Line, on.Name)], _file);

        Assert.Contains(HostsEditor.BlockBegin, File.ReadAllText(_file));
        Assert.Single(HostsTable.Read(_file), r => r.Name == "off.example");
    }

    [Theory]
    [InlineData("1.2.3.4 example.com", "example.com", "1.2.3.4")]
    [InlineData("example.com 1.2.3.4", "example.com", "1.2.3.4")]
    [InlineData("https://www.example.com/path 1.2.3.4 # подпись", "www.example.com", "1.2.3.4")]
    [InlineData("1.2.3.4\ta.example b.example", "b.example", "1.2.3.4")]
    public void A_pasted_list_reads_both_ways(string text, string name, string address)
    {
        var (pins, bad) = HostsTable.ParseList(text);

        Assert.Empty(bad);
        Assert.Equal(address, pins[name]);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4 5.6.7.8 example.com")]
    [InlineData("1.2.3.4 example.com нечто")]
    public void A_line_that_does_not_parse_is_returned(string text)
    {
        var (pins, bad) = HostsTable.ParseList(text);

        Assert.Empty(pins);
        Assert.Single(bad);
    }
}
