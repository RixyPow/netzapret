using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Автонастройка YouTube на «Десинке» (14.11): замер узлов видео, решение по нему,
/// перезапуск движков и перезамер с откатом. Решает библиотека (<see cref="VideoNodesRun"/>,
/// <see cref="YoutubeTuner"/>); здесь — только шаги на экране.
/// </summary>
public partial class DesyncView
{
    /// <summary>Замер до применения и решение по нему — ждут кнопки «Применить».</summary>
    private (VideoNodesReport Before, YoutubeTune Tune)? _youtubePending;

    private CancellationTokenSource? _youtubeWork;

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

    private async void OnYoutubeTune(object sender, RoutedEventArgs e)
    {
        _youtubeWork?.Cancel();
        _youtubeWork = new CancellationTokenSource();
        var token = _youtubeWork.Token;

        YoutubeStart.IsEnabled = false;
        YoutubeSteps.Visibility = Visibility.Visible;
        YoutubeActions.Visibility = Visibility.Collapsed;
        YoutubeReasons.Children.Clear();
        YoutubeProgress.Text = "Шаг 1 из 3 — замер узлов видео: пинг, TCP и QUIC через обход, кэш провайдера. Около 20 секунд…";

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var (report, decision, why) = await Task.Run(() => VideoNodesRun.TuneAsync(settings, token), token);

            if (decision?.Tune is not { } tune || report is null)
            {
                YoutubeProgress.Text = why ?? decision?.Reasons.FirstOrDefault() ?? "Судить не по чему.";
                return;
            }

            YoutubeProgress.Text = $"Шаг 2 из 3 — что поменять. Узлы: {report.Totals}.";
            Reasons(decision.Reasons);

            if (decision.NothingLeft)
                Reason("Десинк для YouTube сделал, что мог; дальше — только «Маршруты», YouTube через VPN.");

            _youtubePending = (report, tune);
            YoutubeApply.Content = tune.Active ? "Применить" : "Записать";
            YoutubeActions.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            YoutubeProgress.Text = "Замер не вышел: " + ex.GetBaseException().Message;
        }
        finally
        {
            YoutubeStart.IsEnabled = true;
        }
    }

    private async void OnYoutubeApply(object sender, RoutedEventArgs e)
    {
        if (_youtubePending is not { } pending)
            return;

        _youtubePending = null;
        YoutubeActions.Visibility = Visibility.Collapsed;
        YoutubeStart.IsEnabled = false;
        var token = (_youtubeWork ??= new CancellationTokenSource()).Token;
        var previous = AppSettings.Load(AppSettings.DefaultPath).Youtube;

        try
        {
            var saved = VideoNodesRun.Save(pending.Tune);
            ShowYoutube(saved);
            ShowChosen(saved);

            YoutubeProgress.Text = "Шаг 3 из 3 — перезапуск движков…";
            var restarted = await EngineControl.RestartAsync("автонастройка YouTube на «Десинке»", token);

            if (!restarted.Ok)
            {
                YoutubeProgress.Text = "Движки не перезапустились: " + restarted.Message + " Настройка записана — подействует со следующего запуска.";
                return;
            }

            YoutubeProgress.Text = "Шаг 3 из 3 — перезамер узлов, около 20 секунд…";
            var (after, _) = await Task.Run(() => VideoNodesRun.MeasureAsync(AppSettings.Load(AppSettings.DefaultPath), token), token);

            if (after is null)
            {
                YoutubeProgress.Text = "Перезамер не вышел — настройка остаётся.";
                return;
            }

            var (worse, said) = VideoNodesRun.Compare(pending.Before, after);
            YoutubeReasons.Children.Clear();
            Reason(said);

            if (!worse)
            {
                YoutubeProgress.Text = "Готово: стало не хуже — настройка остаётся.";
                return;
            }

            ShowYoutube(VideoNodesRun.Save(previous));
            YoutubeProgress.Text = "Стало хуже — прежнее возвращено, движки перезапускаются…";
            var back = await EngineControl.RestartAsync("автонастройка YouTube: откат", token);
            YoutubeProgress.Text = back.Ok ? "Стало хуже — прежнее возвращено." : "Стало хуже — прежнее записано, но движки не перезапустились: " + back.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            YoutubeProgress.Text = "Не вышло: " + ex.GetBaseException().Message;
        }
        finally
        {
            YoutubeStart.IsEnabled = true;
            ShowYoutube(AppSettings.Load(AppSettings.DefaultPath));
        }
    }

    private void OnYoutubeCancel(object sender, RoutedEventArgs e)
    {
        _youtubeWork?.Cancel();
        _youtubePending = null;
        YoutubeSteps.Visibility = Visibility.Collapsed;
        YoutubeStart.IsEnabled = true;
    }

    private async void OnYoutubeOff(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = VideoNodesRun.Save(null);
            ShowYoutube(saved);
            YoutubeSteps.Visibility = Visibility.Visible;
            YoutubeActions.Visibility = Visibility.Collapsed;
            YoutubeReasons.Children.Clear();
            YoutubeProgress.Text = "Автонастройка снята — пресет как есть. Перезапуск движков…";

            var restarted = await EngineControl.RestartAsync("автонастройка YouTube снята на «Десинке»", CancellationToken.None);
            YoutubeProgress.Text = restarted.Ok ? "Автонастройка снята — пресет как есть." : "Снята, но движки не перезапустились: " + restarted.Message;
        }
        catch (Exception ex)
        {
            YoutubeProgress.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private void Reasons(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            Reason(line);
    }

    private void Reason(string line) => YoutubeReasons.Children.Add(new TextBlock
    {
        Text = "•  " + char.ToUpperInvariant(line[0]) + line[1..],
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    });
}
