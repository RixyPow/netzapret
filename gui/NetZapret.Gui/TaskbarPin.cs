using System.IO;
using System.Windows.Threading;
using NetZapret.Core;

namespace NetZapret.Gui;

/// <summary>
/// Закреплённая на панели задач кнопка — со значком в цветах темы (<see cref="PinnedIcon"/>).
/// </summary>
/// <remarks>
/// Владелец 21.10: «при закреплении иконка меняется на дефолтную». Значок
/// ставится ярлыку при запуске, при смене темы и сразу после закрепления:
/// пока программа открыта, папка закреплённых ярлыков под присмотром, и новый
/// ярлык на наш exe получает значок темы, как только Windows его запишет.
/// Пересчёт — с задержкой в полторы секунды: тема в редакторе меняется на
/// каждом шаге, а Windows пишет ярлык не одним движением.
/// </remarks>
internal static class TaskbarPin
{
    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _settle;

    public static void Start(Dispatcher dispatcher)
    {
        _settle = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            Apply();
        }, dispatcher);
        _settle.Stop();

        Themes.Changed += Schedule;

        try
        {
            var folder = PinnedIcon.PinnedFolder;

            if (Directory.Exists(folder))
            {
                _watcher = new FileSystemWatcher(folder, "*.lnk")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };

                // Своя запись ярлыка тоже даёт событие — повторный проход увидит,
                // что значок уже тот, и ничего не тронет.
                FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(Schedule);
                _watcher.Created += changed;
                _watcher.Changed += changed;
                _watcher.Renamed += (_, _) => dispatcher.BeginInvoke(Schedule);
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception)
        {
            // Без присмотра значок ставится при запуске и при смене темы.
        }

        Schedule();
    }

    private static void Schedule()
    {
        _settle?.Stop();
        _settle?.Start();
    }

    private static void Apply()
    {
        if (Environment.ProcessPath is not { } exe)
            return;

        byte[] ico;

        try
        {
            // Картинку рисует WPF — в потоке окна; ярлыки и файл — в фоне.
            var (top, band, bottom) = Emblem.Current();
            ico = Emblem.Ico(top, band, bottom);
        }
        catch (Exception)
        {
            return;
        }

        var folder = Path.GetFullPath("runtime");

        _ = Task.Run(() =>
        {
            try
            {
                if (PinnedIcon.Apply(ico, exe, folder) > 0)
                    Journal.Write("окно", "закреплённой кнопке на панели задач поставлен значок темы");
            }
            catch (Exception)
            {
                // Удобство: не вышло — у закреплённой кнопки останется значок exe.
            }
        });
    }
}
