using System.Windows;
using NetZapret.Core;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Карточка «YouTube» на «Десинке» (14.11): что настроено; «Настроить» открывает окно
/// автонастройки (<see cref="YoutubeTuneWindow"/>) — шаги и таблицы там.
/// </summary>
public partial class DesyncView
{
    private void ShowYoutube(AppSettings settings)
    {
        YoutubeOff.Visibility = settings.Youtube is { Active: true } ? Visibility.Visible : Visibility.Collapsed;

        YoutubeState.Text = settings.Youtube switch
        {
            { Active: true } tune => $"Настроено{When(tune)}: {YoutubeAddon.Describe(tune)}.",
            { } tune => $"Настроено{When(tune)}: десинку для узлов видео ничего не нужно.",
            _ => "Не настроено: работает как в пресете.",
        };
    }

    private static string When(YoutubeTune tune) =>
        tune.Measured is { } at ? $" {at:dd.MM HH:mm}" : string.Empty;

    private void OnYoutubeTune(object sender, RoutedEventArgs e)
    {
        var window = new YoutubeTuneWindow { Owner = Window.GetWindow(this) };

        window.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            ShowYoutube(settings);
            ShowChosen(settings);
        });

        window.Show();
    }

    private async void OnYoutubeOff(object sender, RoutedEventArgs e)
    {
        try
        {
            var before = AppSettings.Load(AppSettings.DefaultPath);
            var saved = VideoNodesRun.Save(null, who: "«Десинк», кнопка «Снять»");
            ShowYoutube(saved);

            // Строка winws2 та же (у пресета свой рецепт) — движки не трогаем.
            if (YoutubeAddon.SameLine(before.Youtube, null, VideoNodesRun.PresetHasRecipe(before)))
                return;

            YoutubeState.Text = "Автонастройка снята — пресет как есть. Перезапуск движков…";

            var restarted = await EngineControl.RestartAsync("автонастройка YouTube снята на «Десинке»", CancellationToken.None);
            YoutubeState.Text = restarted.Ok ? "Не настроено: работает как в пресете." : "Снята, но движки не перезапустились: " + restarted.Message;
        }
        catch (Exception ex)
        {
            YoutubeState.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }
}
