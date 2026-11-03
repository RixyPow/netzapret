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
