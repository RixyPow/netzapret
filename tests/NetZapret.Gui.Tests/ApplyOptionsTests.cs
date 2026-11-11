using NetZapret.Core.Updates;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>Аргументы окна подмены доходят от программы до копии без потерь.</summary>
public sealed class ApplyOptionsTests
{
    [Fact]
    public void ArgumentsRoundTrip()
    {
        var plan = new UpdatePlan { StagedAt = @"C:\Users\Иван Тест\NetZapret\runtime\update\files\NetZapret", Files = 1, Kept = [] };
        var arguments = UpdateInstaller.ApplierArguments(4242, plan, @"C:\Users\Иван Тест\NetZapret\", "0.15.2", "0.15.3", "112 МБ через туннель", null);

        var options = ApplyOptions.Parse([.. arguments]);

        Assert.NotNull(options);
        Assert.Equal(4242, options.ProcessId);
        Assert.Equal(plan.StagedAt, options.Source);
        Assert.Equal(@"C:\Users\Иван Тест\NetZapret", options.Target);
        Assert.Equal("0.15.3", options.To);
        Assert.Equal("112 МБ через туннель", options.Downloaded);
        Assert.Null(options.NotesFile);
        Assert.False(options.ToTray);
        Assert.Null(ApplyOptions.Parse(["--tray"]));
    }

    /// <summary>
    /// Поставленное само при автозапуске возвращается в трей — тем же ключом,
    /// которым зовёт задача планировщика (12.11).
    /// </summary>
    [Fact]
    public void TrayRelaunchSurvives()
    {
        var plan = new UpdatePlan { StagedAt = @"C:\NetZapret\runtime\update\files\NetZapret", Files = 1, Kept = [] };
        var arguments = UpdateInstaller.ApplierArguments(7, plan, @"C:\NetZapret", "1.5.5", "1.6.0", "120 МБ напрямую", null, toTray: true);

        Assert.True(ApplyOptions.Parse([.. arguments])!.ToTray);
        Assert.Equal(TrayIcon.Switch, UpdateInstaller.TraySwitch);
    }
}
