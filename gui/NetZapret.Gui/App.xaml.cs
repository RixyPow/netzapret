using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NetZapret.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ставится первым делом, до всего остального. Окно без консоли,
        // упавшее до появления, просто исчезает: спросили права — и тишина.
        // Ровно это и случилось на первом запуске, и полчаса ушло на то,
        // чтобы узнать причину, которую программа знала сразу.
        DispatcherUnhandledException += ShowAndKeepRunning;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Show(args.ExceptionObject as Exception, fatal: true);

        // Тот же поиск, что делает консоль: все пути в настройках, правилах
        // и списках заданы относительно корня установки. У программы,
        // запущенной с повышением прав, рабочим каталогом оказывается
        // System32, и без перехода окно читало бы конфиг оттуда.
        MoveToInstallDirectory();

        // Роли без окна: супервизор и остановка. StartupUri из разметки
        // обрабатывается уже после этого метода, поэтому снять его достаточно
        // здесь — иначе рядом с движками открылось бы второе окно.
        if (e.Args.Contains(SupervisorHost.Switch))
        {
            _headless = true;
            StartupUri = null!;

            RunAsSupervisor(e.Args);
            return;
        }

        if (e.Args.Contains(SupervisorHost.StopSwitch))
        {
            _headless = true;
            StartupUri = null!;

            RunStop();
        }
    }

    private async void RunStop()
    {
        await SupervisorHost.StopAsync(CancellationToken.None);

        Shutdown(0);
    }

    /// <summary>
    /// Работает супервизором вместо показа окна.
    /// </summary>
    /// <remarks>
    /// Ctrl+C у процесса без консоли нет: останавливает его само окно, убивая
    /// по PID из файла состояния. Поэтому отмены здесь нет — выход только
    /// по завершению работы супервизора.
    /// </remarks>
    private async void RunAsSupervisor(string[] args)
    {
        var code = await SupervisorHost.RunAsync(args, CancellationToken.None);

        Shutdown(code);
    }

    /// <summary>Показывать сбои некому: процесс работает без окна.</summary>
    private static bool _headless;

    /// <summary>
    /// Показывает сбой и продолжает работу, если это возможно.
    /// </summary>
    /// <remarks>
    /// Сбой в одном разделе не повод закрывать окно: остальные пять работают,
    /// и человек, у которого не читается подписка, вправе дойти до проверки
    /// блокировок. Необработанное исключение при этом не замалчивается —
    /// оно названо, с местом в коде.
    /// </remarks>
    private void ShowAndKeepRunning(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Show(e.Exception, fatal: false);
        e.Handled = true;
    }

    /// <summary>О чём уже сказали.</summary>
    /// <remarks>
    /// Сбой при отрисовке повторяется на каждом кадре, и без этого набора
    /// окно с сообщением открывалось поверх предыдущего снова и снова:
    /// первый же отказ шрифта дал два окна подряд, и закрыть их получалось
    /// не сразу. Один и тот же сбой стоит показать один раз.
    /// </remarks>
    private static readonly HashSet<string> Told = [];

    private static void Show(Exception? error, bool fatal)
    {
        if (error is null)
            return;

        if (!Told.Add(error.GetType().Name + ": " + error.Message))
            return;

        var text = fatal
            ? "Произошёл сбой, и окно закроется.\n\n"
            : "Произошёл сбой. Окно продолжит работу, но этот раздел мог не досчитать.\n\n";

        text += error.GetType().Name + ": " + error.Message;

        // Место в коде — в конце и мелочью. Человеку оно ни к чему, а нам
        // без него остаётся гадать по одному сообщению.
        if (error.StackTrace is { } stack)
            text += "\n\n" + string.Join('\n', stack.Split('\n').Take(4)).TrimEnd();

        try
        {
            var log = Path.Combine("logs", "gui.log");
            Directory.CreateDirectory("logs");

            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {error}\n\n");
            text += $"\n\nЗаписано в {Path.GetFullPath(log)}";
        }
        catch (Exception)
        {
            // Не записалось — не беда, показать всё равно показали.
        }

        // В роли супервизора окна нет, и показывать сообщение некому: оно
        // повисло бы невидимым модальным окном у процесса, который держит
        // весь трафик машины. Запись в журнал выше уже сделана.
        if (_headless)
            return;

        MessageBox.Show(
            text,
            "NetZapret",
            MessageBoxButton.OK,
            fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
    }

    private static void MoveToInstallDirectory()
    {
        if (Directory.Exists("config"))
            return;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "config")))
            {
                Directory.SetCurrentDirectory(directory.FullName);
                return;
            }

            directory = directory.Parent;
        }

        // Не нашли — оставляем как есть. Разделы скажут об этом сами, каждый
        // про своё: так понятнее, чем одно окно с ошибкой при запуске.
    }
}
