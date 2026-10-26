using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Журнал»: последние строки движка — из состояния хозяина (NzBox.status).
/// </summary>
/// <remarks>
/// Секретов в журнале нет по договору (android/DESIGN.md §4: ссылки подписок,
/// ключи и пароль ByeDPI не пишутся ни в журнал, ни в состояние), поэтому
/// им можно поделиться как есть.
/// </remarks>
internal sealed class LogSection : Section
{
    private readonly SelectableTextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Classes = { "mono" },
    };

    private DispatcherTimer? _poll;

    public LogSection(MainView shell) : base(shell)
    {
        var share = Ui.Button("Поделиться журналом", () => _ = ShareAsync());
        share.HorizontalAlignment = HorizontalAlignment.Stretch;

        Body.Children.Add(share);
        Body.Children.Add(Ui.Card(_text));
    }

    public override string Title => "Журнал";

    protected override void Shown()
    {
        Render();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += (_, _) => Render();
        _poll.Start();
    }

    protected override void Hidden()
    {
        _poll?.Stop();
        _poll = null;
    }

    private void Render()
    {
        var log = Host.Status.Log;
        _text.Text = log.Count == 0
            ? "Пусто: журнал заполняется, пока движки работают."
            : string.Join("\n", log);
    }

    private async Task ShareAsync()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine("runtime", "engine-log.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, string.Join("\n", Host.Status.Log), new UTF8Encoding(false));
            await Host.ShareFileAsync(path, "text/plain");
        }
        catch (Exception ex)
        {
            Shell.Say("Не вышло: " + ex.GetBaseException().Message);
        }
    }
}
