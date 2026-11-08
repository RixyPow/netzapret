using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Выбор подделки у секции пресета (обсуждение №51): подставляется в строку
/// запуска, файл пресета не трогается.
/// </summary>
public sealed class FakeChoiceTests
{
    private const string Voice = "Голосовые звонки/чаты";

    [Fact]
    public void Voice_section_of_v12_lite_has_a_udp_fake_slot()
    {
        var slots = FakeChoice.Slots(V12Lite());
        var voice = Assert.Single(slots, s => s.Section == Voice);

        Assert.Equal("quic_google", voice.Blob);
        Assert.True(voice.Udp);
        Assert.Equal(0, voice.Slot);
        Assert.Contains("discord", voice.Hints, StringComparison.Ordinal);

        // discord.com — hostfakesplit без образца, места подделки там нет.
        Assert.DoesNotContain(slots, s => s.Section == "discord.com");
        Assert.Contains(slots, s => s.Section == "discord.media (voice RTC)" && s.Blob == "tls_google" && !s.Udp);
    }

    [Fact]
    public void Chosen_fake_replaces_only_its_slot_and_is_declared()
    {
        var preset = V12Lite();
        var before = WinwsCommandLine.Build(preset).ToList();
        var after = before.ToList();

        // Так у SuzuyaJ74 в №30: stun2 вместо quic_google у голоса.
        int replaced = FakeChoice.Apply(after, "Universal V12 Lite", [new("Universal V12 Lite", Voice, 0, "stun2.bin")]);

        Assert.Equal(1, replaced);
        Assert.Equal("--blob=nz_fake_stun2:@bin/stun2.bin", after[0]);
        Assert.Contains("--lua-desync=fake:blob=nz_fake_stun2:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:payload=all:repeats=10", after);

        // Всё прочее — побайтно как было.
        var changed = after.Skip(1).Zip(before).Count(p => p.First != p.Second);
        Assert.Equal(1, changed);
        Assert.Equal(before.Count + 1, after.Count);
    }

    [Fact]
    public void Choice_of_another_preset_or_a_vanished_section_changes_nothing()
    {
        var before = WinwsCommandLine.Build(V12Lite()).ToList();
        var after = before.ToList();

        int replaced = FakeChoice.Apply(after, "Universal V12 Lite",
        [
            new("Universal V11 Lite", Voice, 0, "stun2.bin"),
            new("Universal V12 Lite", "Секции такой нет", 0, "stun2.bin"),
            new("Universal V12 Lite", Voice, 5, "stun2.bin"),
        ]);

        Assert.Equal(0, replaced);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Built_in_fake_is_used_by_name_without_declaration()
    {
        var after = WinwsCommandLine.Build(V12Lite()).ToList();
        int count = after.Count;

        FakeChoice.Apply(after, "Universal V12 Lite", [new("Universal V12 Lite", Voice, 0, "fake_default_quic")]);

        Assert.Equal(count, after.Count);
        Assert.Contains(after, a => a.StartsWith("--lua-desync=fake:blob=fake_default_quic:", StringComparison.Ordinal));
    }

    [Fact]
    public void Udp_slot_is_offered_quic_and_udp_samples_only()
    {
        var available = FakeChoice.Available(Path.Combine(RepoRoot(), "vendor", "zapret-gui"));
        var voice = FakeChoice.Slots(V12Lite()).Single(s => s.Section == Voice);
        var fitting = FakeChoice.Fitting(voice, available).Select(b => b.Id).ToList();

        Assert.Contains("stun2.bin", fitting);
        Assert.Contains("ACTIVE_DISCORD_UDP.bin", fitting);
        Assert.Contains("quic_initial_www_google_com.bin", fitting);
        Assert.DoesNotContain("tls_clienthello_sochi_park.bin", fitting);
        Assert.Equal(FakeKind.Tls, FakeChoice.KindOf("nz-tls_clienthello_awswaf_com.bin"));
    }

    /// <summary>
    /// ACTIVE_DISCORD_UDP.bin копии Zapret GUI — пакет QUIC Steam, а не Discord (PR Flowseal №18613,
    /// сверено хэшем 08.11): вид по содержимому, в пометке — чья копия.
    /// </summary>
    [Fact]
    public void A_misnamed_sample_is_marked_by_what_is_inside()
    {
        var available = FakeChoice.Available(Path.Combine(RepoRoot(), "vendor", "zapret-gui"));
        var discord = available.Single(b => b.Id == "ACTIVE_DISCORD_UDP.bin");

        Assert.Equal(FakeKind.Quic, discord.Kind);
        Assert.Contains("копия quic_initial_steamcommunity_com.bin", discord.Note, StringComparison.Ordinal);

        // Честные имена не помечаются, даже если у них есть двойник.
        Assert.Null(available.Single(b => b.Id == "quic_initial_steamcommunity_com.bin").Note);
        Assert.Null(available.Single(b => b.Id == "stun2.bin").Note);
    }

    [Fact]
    public void Kind_is_read_from_the_first_bytes()
    {
        Assert.Equal(FakeKind.Tls, FakeChoice.ContentKind([0x16, 0x03, 0x01, 0x02, 0x00]));
        Assert.Equal(FakeKind.Quic, FakeChoice.ContentKind([0xC5, 0x00, 0x00, 0x00, 0x01, 0x14]));
        Assert.Equal(FakeKind.Quic, FakeChoice.ContentKind([0xD3, 0x6B, 0x33, 0x43, 0xCF]));

        var stun = new byte[20];
        stun[1] = 0x01;
        stun[4] = 0x21; stun[5] = 0x12; stun[6] = 0xA4; stun[7] = 0x42;
        Assert.Equal(FakeKind.Udp, FakeChoice.ContentKind(stun));

        Assert.Null(FakeChoice.ContentKind([0x00, 0x01, 0x02]));
        Assert.Null(FakeChoice.ContentKind([0xC0, 0x12, 0x34, 0x56, 0x78]));
    }

    private static ZapretPreset V12Lite() =>
        new PresetReader().Load(Path.Combine(RepoRoot(), "presets", "Universal V12 Lite.txt"));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("нет корня репозитория");
    }
}
