using NetZapret.Core.Connections;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Наблюдение ради автопополнения — пока программа запущена (владелец 19.10).</summary>
public sealed class ProgramPresenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Wanted = ["REPO.exe"];

    [Fact]
    public void Nothing_happens_while_the_program_is_not_running()
    {
        var presence = new ProgramPresence();

        Assert.Equal(PresenceChange.None, presence.Update(["chrome", "Discord"], Wanted, Start));
        Assert.False(presence.Present);
    }

    [Fact]
    public void Launch_is_seen_once_by_the_name_without_exe_in_any_case()
    {
        var presence = new ProgramPresence();

        Assert.Equal(PresenceChange.Appeared, presence.Update(["chrome", "repo"], Wanted, Start));
        Assert.Equal("REPO.exe", presence.Seen);
        Assert.Equal(PresenceChange.None, presence.Update(["repo"], Wanted, Start.AddSeconds(3)));
    }

    [Fact]
    public void Exit_counts_only_after_the_grace_so_a_restart_does_not_cut_the_watch()
    {
        var presence = new ProgramPresence();
        presence.Update(["REPO"], Wanted, Start);

        // Перезапуск после обновления: десять секунд без процесса.
        Assert.Equal(PresenceChange.None, presence.Update([], Wanted, Start.AddSeconds(10)));
        Assert.Equal(PresenceChange.None, presence.Update(["REPO"], Wanted, Start.AddSeconds(20)));

        Assert.Equal(PresenceChange.None, presence.Update([], Wanted, Start.AddSeconds(40)));
        Assert.Equal(PresenceChange.Gone, presence.Update([], Wanted, Start.AddSeconds(50)));
        Assert.False(presence.Present);
    }

    [Fact]
    public void Turning_auto_fill_off_counts_as_the_program_gone()
    {
        var presence = new ProgramPresence();
        presence.Update(["REPO"], Wanted, Start);

        Assert.Equal(PresenceChange.Gone, presence.Update(["REPO"], [], Start + ProgramPresence.Grace));
    }
}
