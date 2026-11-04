using NetZapret.Gui.Views;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Строки состояния автозамены в карточке «Адреса в hosts» и плашка посредника
/// в «Файле hosts» (05.11).
/// </summary>
public sealed class PinWatchLineTests
{
    private static readonly DateTimeOffset Now = new DateTime(2026, 11, 5, 15, 0, 0, DateTimeKind.Local);

    private static PinWatch Watch(DateTimeOffset? last, params string[] suspects) =>
        new(last, suspects, [new("Comss DNS", 30), new("Zapret DNS", 21), new(null, 1)]);

    [Fact]
    public void Names_the_last_check_and_the_next_one()
    {
        var line = DnsView.WhenLine(Watch(Now.AddHours(-1)), auto: true, Now);

        Assert.Equal("Проверено в 14:00, следующая около 20:00", line);
    }

    [Fact]
    public void Overdue_check_waits_for_the_engines_not_for_an_hour()
    {
        var line = DnsView.WhenLine(Watch(Now.AddHours(-7)), auto: true, Now);

        Assert.Contains("следующая — как только движки", line);
    }

    [Fact]
    public void Switched_off_says_so_instead_of_the_next_check()
    {
        Assert.Equal("Проверено в 14:00, автозамена выключена", DnsView.WhenLine(Watch(Now.AddHours(-1)), auto: false, Now));
        Assert.Equal("Ещё не проверялось, автозамена выключена", DnsView.WhenLine(Watch(null), auto: false, Now));
    }

    [Fact]
    public void Suspects_are_counted_and_the_first_three_named()
    {
        var line = DnsView.WhenLine(Watch(Now.AddHours(-1), "1.1.1.1", "2.2.2.2", "3.3.3.3", "4.4.4.4"), auto: true, Now);

        Assert.EndsWith(" · под подозрением: 4 (1.1.1.1, 2.2.2.2, 3.3.3.3 и ещё 1)", line);
    }

    [Fact]
    public void Watched_pins_are_listed_by_intermediary()
    {
        Assert.Equal("Под присмотром: Comss DNS 30, Zapret DNS 21, без посредника 1", DnsView.WatchedLine(Watch(null)));
        Assert.Contains("нет", DnsView.WatchedLine(new PinWatch(null, [], [])));
    }

    private static HostsLine Row(string? note, bool ours = true) =>
        new(new HostsRow
        {
            Line = 0,
            Name = "claude.ai",
            Address = "5.129.221.64",
            Enabled = true,
            Note = note,
            Source = ours ? HostsTable.OurSource : "Zapret GUI",
            Ours = ours,
            Kind = HostsPinKind.Proxy,
        });

    [Fact]
    public void Via_mark_becomes_a_badge_and_stays_out_of_the_caption()
    {
        var line = Row("Claude " + HostsEditor.ViaMark + "zapret");

        Assert.Equal("Zapret DNS", line.Via);
        Assert.True(line.HasVia);
        Assert.Equal("Claude", line.Note);
    }

    [Fact]
    public void No_badge_without_the_mark_or_on_a_foreign_line()
    {
        Assert.Null(Row(null).Via);
        Assert.Null(Row("Claude").Via);
        Assert.Null(Row(HostsEditor.ViaMark + "comss", ours: false).Via);
        Assert.Equal("gone", Row(HostsEditor.ViaMark + "gone").Via);
    }

    [Fact]
    public void Relay_check_is_named_only_when_it_confirmed_the_address()
    {
        var zapret = IntermediaryDns.ByKey("zapret")!;

        var byControls = PinWindow.NowNote(new RelayNow(zapret, "claude.ai", RelayVerdict.Relays, ["5.129.221.64"]));
        var byCheck = PinWindow.NowNote(new RelayNow(zapret, "claude.ai", RelayVerdict.Relays, ["5.129.221.64"], ByRelayCheck: true));

        Assert.DoesNotContain("relay.check", byControls);
        Assert.Contains("relay.check", byCheck);
    }

    [Fact]
    public void Lost_answer_and_unconfirmed_address_are_not_called_refusal()
    {
        var astracat = IntermediaryDns.ByKey("astracat")!;

        var lost = PinWindow.NowNote(new RelayNow(astracat, "autodesk.com", RelayVerdict.Unanswered, []));
        var unsure = PinWindow.NowNote(new RelayNow(astracat, "autodesk.com", RelayVerdict.Unconfirmed, ["217.60.179.6"]));

        Assert.DoesNotContain("не проксирует", lost);
        Assert.DoesNotContain("не проксирует", unsure);
        Assert.Contains("217.60.179.6", unsure);
    }
}
