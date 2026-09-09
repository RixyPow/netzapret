using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Updates;

namespace NetZapret.Gui.Views;

/// <summary>Выключатель без своего раздела.</summary>
public sealed record FlagRow(string Key, string Name, string Note)
{
    public bool On { get; set; }

    public string State => On ? "включено" : "выключено";

    public Brush Color => (Brush)Application.Current.FindResource(On ? "Accent" : "Muted");
}

/// <summary>
/// Всё, что не заслужило своего раздела, но нужно.
/// </summary>
/// <remarks>
/// Режим и автозапуск отсюда ушли на «Главную», а подписки — в «VPN».
/// Здесь они и правда лежали по остаточному принципу: режим задаёт рамку,
/// внутри которой имеют смысл маршруты, и место ему рядом с кнопкой запуска,
/// а подписке — там, где видно, что она приносит.
/// </remarks>
public partial class MoreView : UserControl
{
    private CancellationTokenSource? _work;

    public MoreView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _work?.Cancel();
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowVersion(settings);
        ShowFlags(settings);

        RootValue.Text = Path.GetFullPath(".");

        Status.Text = "Изменения записываются сразу, а действовать начинают при следующем "
            + "запуске движков.";

        StartDefenderCheck();
    }

    private void ShowVersion(AppSettings settings)
    {
        VersionValue.Text = UpdateCheck.Current;

        UpdateValue.Text = settings.CheckForUpdates
            ? "Проверяется при запуске консоли."
            : "Проверка при запуске выключена.";
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();

        UpdateButton.IsEnabled = false;
        UpdateValue.Text = "Спрашиваю GitHub…";

        try
        {
            var release = await UpdateCheck.LatestAsync(_work.Token);

            UpdateValue.Text = release is null
                ? "Не удалось узнать: GitHub не ответил."
                : UpdateCheck.IsNewer(release.Version, UpdateCheck.Current)
                    ? $"Есть новее: {release.Version}. Ставится из консоли — обновление трогает "
                      + "движки, которые сейчас несут весь трафик машины."
                    : "Установлена последняя.";
        }
        catch (OperationCanceledException)
        {
            UpdateValue.Text = "Проверка прервана.";
        }
        catch (Exception ex)
        {
            UpdateValue.Text = "Не удалось узнать: " + ex.GetBaseException().Message;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Спрашивает Defender, исключена ли наша папка.
    /// </summary>
    /// <remarks>
    /// В стороне от показа: <c>Get-MpPreference</c> поднимает PowerShell
    /// и отвечает секунду-полторы, а раздел должен открыться сразу.
    /// </remarks>
    private void StartDefenderCheck()
    {
        DefenderValue.Text = "спрашиваю…";
        DefenderButton.IsEnabled = false;

        var path = Path.GetFullPath(".");

        _ = Task.Run(() =>
        {
            bool excluded = IsExcluded(path);

            Dispatcher.Invoke(() =>
            {
                DefenderValue.Text = excluded ? "есть" : "нет";
                DefenderButton.IsEnabled = !excluded;
            });
        });
    }

    private void OnExclude(object sender, RoutedEventArgs e)
    {
        var path = Path.GetFullPath(".");

        DefenderButton.IsEnabled = false;
        Status.Text = "Прошу Defender об исключении…";

        _ = Task.Run(() =>
        {
            int code = Run(
                "powershell",
                $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{path.Replace("'", "''")}'\"");

            // Проверяем делом, а не кодом возврата: при включённой защите
            // от изменений команда проходит и не делает ничего.
            bool excluded = IsExcluded(path);

            Dispatcher.Invoke(() =>
            {
                DefenderValue.Text = excluded ? "есть" : "нет";
                DefenderButton.IsEnabled = !excluded;

                Status.Text = excluded
                    ? $"В исключениях: {path}"
                    : code == 0
                        ? "Команда прошла, а исключение не появилось — включена защита от "
                          + "изменений. Добавьте вручную: Безопасность Windows → Защита от "
                          + "вирусов → Исключения."
                        : $"Не вышло, код {code}.";
            });
        });
    }

    private static bool IsExcluded(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-MpPreference).ExclusionPath -join [char]10\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return output.Split('\n').Any(line => string.Equals(
                line.Trim().TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Defender может быть выключен, подменён другим антивирусом или
            // не отвечать вовсе. Ни один из случаев не повод не открыть раздел.
            return false;
        }
    }

    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return -1;

            process.WaitForExit(30_000);
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void ShowFlags(AppSettings settings)
    {
        var rows = new List<FlagRow>
        {
            new("logs", "Вести журнал",
                "Без него разбор сбоя сводится к догадкам. Выключают те, кого смущает рост "
                + "файла: winws2 пишет строку на соединение.")
            { On = settings.LogsEnabled },

            new("verify", "Проверять проход трафика",
                "Не только открытость порта, но и то, что через него что-то проходит. "
                + "Дороже по времени, зато ловит молчащую трубу.")
            { On = settings.VerifyTraffic },

            new("foreign", "Автоподбор только по зарубежным",
                "Подбор идёт по задержке и потому всегда сползает на ближайший сервер — свой "
                + "же, — а через него не работает ровно то, ради чего туннель обычно и нужен.")
            { On = settings.ForeignExitsOnly },

            new("discord", "Предлагать перезапуск Discord",
                "Он запоминает голосовые серверы на сеанс и до перезапуска ходит по-старому. "
                + "Сам он не перезапускается никогда: посреди звонка это хуже задержки.")
            { On = settings.OfferDiscordRestart },

            new("updates", "Искать обновления при запуске",
                "Только искать. Установка остаётся отдельным действием с отдельным согласием.")
            { On = settings.CheckForUpdates },
        };

        Flags.ItemsSource = rows;
    }

    private void OnFlag(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            settings = key switch
            {
                "logs" => settings with { LogsEnabled = !settings.LogsEnabled },
                "verify" => settings with { VerifyTraffic = !settings.VerifyTraffic },
                "foreign" => settings with { ForeignExitsOnly = !settings.ForeignExitsOnly },
                "discord" => settings with { OfferDiscordRestart = !settings.OfferDiscordRestart },
                _ => settings with { CheckForUpdates = !settings.CheckForUpdates },
            };

            settings.Save(AppSettings.DefaultPath);

            ShowFlags(settings);
            ShowVersion(settings);

            Status.Text = "Записано. Применится при следующем запуске движков.";
            this.Offer("Настройка изменена");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder })
            return;

        var target = Path.GetFullPath(folder);

        if (!Directory.Exists(target))
        {
            Status.Text = $"Папки ещё нет: {target}. Она заводится, когда в ней появляется первый файл.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть: " + ex.GetBaseException().Message;
        }
    }
}
