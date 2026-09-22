using System.IO;
using System.Windows;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

public partial class App : Application
{
    /// <summary>
    /// Ключи, которые программа понимает.
    /// </summary>
    /// <remarks>
    /// Перечислены в одном месте намеренно, хотя разбираются в трёх разных:
    /// два здесь, один у значка в трее, пять у супервизора. Проверка
    /// на незнакомый ключ обязана видеть их все сразу, иначе она объявляет
    /// незнакомым то, до чего разбор просто ещё не дошёл.
    ///
    /// Так и ломался автозапуск. Задача в планировщике зовёт программу
    /// с <c>--tray</c>, а он разбирается ниже проверки — и программа отвечала
    /// «ключ не поддерживается» на собственный ключ, выходя с кодом 2.
    /// Увидеть это можно было только в планировщике: окно с жалобой в сеансе
    /// автозапуска показывать некому.
    /// </remarks>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        SupervisorHost.Switch,      // --supervisor
        SupervisorHost.StopSwitch,  // --stop
        TrayIcon.Switch,            // --tray
        Headless.StartSwitch,       // --start

        // Разбираются супервизором: он получает ту же командную строку.
        "--no-proxy",
        "--verify-traffic",
        "--proxy-config",
        "--preset",
        "--log",
    };

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

        static bool Unknown(string argument) =>
            argument.StartsWith("--", StringComparison.Ordinal)
            && !Known.Contains(argument.Split('=')[0]);

        // Тот же поиск, что делает консоль: все пути в настройках, правилах
        // и списках заданы относительно корня установки. У программы,
        // запущенной с повышением прав, рабочим каталогом оказывается
        // System32, и без перехода окно читало бы конфиг оттуда.
        MoveToInstallDirectory();

        // Роли без окна: супервизор и остановка.
        //
        // Окно открывается здесь, а не через StartupUri в разметке. Снять
        // StartupUri для безоконных ролей нельзя: свойство не принимает null
        // и бросает ArgumentNullException прямо отсюда — а обработчик сбоев
        // выше её проглатывал, и процесс оставался жить пустым, ничего
        // не запустив. Каждое нажатие «Запустить» плодило такого зомби.
        if (e.Args.Contains(SupervisorHost.Switch))
        {
            _headless = true;
            RunAsSupervisor(e.Args);

            return;
        }

        if (e.Args.Contains(SupervisorHost.StopSwitch))
        {
            _headless = true;
            RunStop();

            return;
        }

        // Поднять движки из командной строки — тем же кодом, что и кнопкой.
        // Нужно для разбора неисправностей: поднять и погасить, не открывая
        // окна. Своей логики у ключа нет ни строки — см. Headless.
        //
        // Спросить состояние здесь нечем, и намеренно: окно требует прав
        // администратора, и такой вопрос дёргал бы UAC каждый раз. Читать
        // состояние прав не нужно вовсе — для этого есть tools/nz.
        if (e.Args.Contains(Headless.StartSwitch))
        {
            _headless = true;
            RunStart();

            return;
        }

        // Незнакомый ключ не открывает окно. Скрипт сборки однажды позвал
        // программу с ключом, которого она не знает, — та открылась обычным
        // окном, а cmd остался ждать, пока его закроют: сборка висела на строке
        // остановки, запустив ровно то, что просила остановить.
        //
        // Проверяется только то, что похоже на ключ. Путь к файлу аргументом
        // быть законно может — например, когда пресет перетаскивают на значок.
        //
        // Перечень нужен весь и до проверки. Прежде здесь стояли только те
        // два ключа, что разбираются выше, а --tray разбирается ниже — и
        // проверка успевала объявить его незнакомым раньше, чем до него
        // доходило дело. Ровно так и ломался автозапуск: задача в планировщике
        // зовёт программу с --tray, программа отвечает «ключ не поддерживается»
        // и выходит с кодом 2. В планировщике это выглядит как «последний
        // результат: 2» — и больше нигде, потому что окно с жалобой в сеансе
        // автозапуска показать некому.
        if (e.Args.FirstOrDefault(Unknown) is { } unknown)
        {
            _headless = true;

            Show(new ArgumentException($"Ключ {unknown} не поддерживается."), fatal: true);
            Shutdown(2);

            return;
        }

        // Интерфейс — в одном экземпляре. Автозапуск и запуск руками иначе
        // дают две иконки в трее и два окна, каждое со своим опросом
        // состояния и своими кнопками к одним и тем же движкам.
        if (!SingleInstance.Claim())
        {
            SingleInstance.RequestShow();
            Shutdown(0);

            return;
        }

        // Остатки прошлого обновления. Сценарий подмены не может стереть
        // каталог, из которого исполняется, и убирает его следующий запуск.
        // Звала это только консоль, при каждом старте; окно, обновлявшее
        // себя тем же UpdateInstaller, не звало ни разу — распакованный
        // архив оставался в runtime\update навсегда. Найдено 23.09 при
        // удалении консоли.
        Core.Updates.UpdateInstaller.CleanUp();

        // Закрытие окна больше не закрывает программу: крестик прячет её
        // в трей. Выход остаётся явным — пунктом в меню значка.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _tray = new TrayIcon();
        SingleInstance.OnShowRequested(TrayIcon.Show);

        if (e.Args.Contains(TrayIcon.Switch))
        {
            // Автозапуск: окна нет, движки поднимаются сами. Иначе задача
            // в планировщике только показывала бы значок, а обход ждал бы,
            // пока человек откроет окно и нажмёт кнопку.
            _ = StartOnLogonAsync();

            return;
        }

        // До создания окна: иначе оно мигнёт тёмным и перекрасится на глазах.
        Themes.Apply(Themes.Parse(AppSettings.Load(AppSettings.DefaultPath).Theme));

        new MainWindow().Show();
    }

    /// <summary>
    /// Поднимает движки при входе в систему.
    /// </summary>
    /// <remarks>
    /// <para>
    /// С повторами и с записью в журнал, и то и другое по делу. Прежде вызов
    /// стоял брошенной задачей — <c>_ = StartAsync(...)</c>, — и его исход
    /// не проверял никто: значок появлялся, движки не поднимались, и узнать
    /// причину было негде. Окна в этот момент нет, сказать человеку нечем,
    /// поэтому исход пишется туда же, куда пишет супервизор.
    /// </para>
    /// <para>
    /// Повторы нужны из-за того, когда именно это происходит. Задача срабатывает
    /// по входу в систему, а сеть к этому моменту поднимается не всегда:
    /// адаптер ещё договаривается, DNS не отвечает, подписка не читается.
    /// Через полминуты то же самое обычно проходит.
    /// </para>
    /// <para>
    /// <b>Успехом считается поднявшийся движок, а не запустившийся супервизор.</b>
    /// Это исправление жалобы 19.09: «трей запускается, программа запускается,
    /// а движки нужно поднимать кнопкой». <see cref="EngineControl.StartAsync"/>
    /// отвечает «Движки поднимаются» сразу, как только порождён процесс
    /// супервизора, — дальше тот может пять раз не поднять sing-box и сдаться,
    /// а в журнале уже стоит «движки подняты с попытки 1». Ровно это и стояло
    /// там семь раз подряд, и повторы, заведённые ради неготовой сети,
    /// не срабатывали ни разу: первая попытка всегда «удавалась».
    /// </para>
    /// </remarks>
    private static async Task StartOnLogonAsync()
    {
        TimeSpan[] waits = [TimeSpan.Zero, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45)];

        for (int attempt = 0; attempt < waits.Length; attempt++)
        {
            if (waits[attempt] > TimeSpan.Zero)
                await Task.Delay(waits[attempt]);

            try
            {
                // Кто-то мог успеть поднять их руками, пока мы ждали.
                //
                // Мерка — «работают», а не «здоровы». Дело автозапуска
                // поднять движки, а не починить чужую подписку: служба,
                // чей процесс жив, поднята, даже если наружу не доходит.
                if (EngineHealth.Running(State()))
                {
                    Note($"автозапуск: движки уже работают (попытка {attempt + 1})");
                    return;
                }

                // Недоподнятое надо снять: второй супервизор поверх первого
                // дерётся с ним за TUN и WinDivert, и проигрывают оба.
                if (State()?.IsSupervisorAlive() == true)
                    await EngineControl.StopAsync(CancellationToken.None);

                var outcome = await EngineControl.StartAsync(CancellationToken.None);

                if (!outcome.Ok)
                {
                    Note($"автозапуск, попытка {attempt + 1} из {waits.Length}: {outcome.Message}");
                    continue;
                }

                if (await WaitUntilHealthyAsync())
                {
                    Note($"автозапуск: движки подняты с попытки {attempt + 1}");
                    return;
                }

                Note($"автозапуск, попытка {attempt + 1} из {waits.Length}: "
                    + EngineHealth.Complaint(State()));
            }
            catch (Exception ex)
            {
                Note($"автозапуск, попытка {attempt + 1} из {waits.Length}: "
                    + ex.GetBaseException().Message);
            }
        }

        Note("автозапуск: движки поднять не удалось. Откройте окно и запустите руками.");
    }

    /// <summary>
    /// Сколько ждать, пока движки поднимутся, прежде чем счесть попытку неудачной.
    /// </summary>
    /// <remarks>
    /// С запасом: супервизор перезапускает упавшую службу пять раз с растущим
    /// ожиданием — 2, 4, 8, 16, 32 секунды, — и ровно такая лесенка вытянула
    /// sing-box 19.09 в 11:17, когда первый запуск завершился кодом 1. Срок
    /// короче этой лесенки объявлял бы неудачей то, что само чинится.
    /// </remarks>
    private static readonly TimeSpan HealthyWithin = TimeSpan.FromSeconds(75);

    /// <summary>
    /// Ждёт, пока службы не поднимутся.
    /// </summary>
    /// <remarks>
    /// Именно поднимутся, а не станут здоровыми. Ждать здоровья значит
    /// ждать живой подписки — а её может не быть неделями, и всё это время
    /// автозапуск сносил бы исправные движки и поднимал заново.
    /// </remarks>
    private static async Task<bool> WaitUntilHealthyAsync()
    {
        var until = DateTime.UtcNow + HealthyWithin;

        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            if (EngineHealth.Running(State()))
                return true;

            // Супервизор сдался — ждать больше нечего, его лесенка кончилась.
            if (State() is { } state && !state.IsSupervisorAlive())
                return false;
        }

        return false;
    }

    /// <summary>Состояние, оставленное супервизором.</summary>
    private static SupervisorState? State() => SupervisorState.Load(SupervisorState.DefaultPath);

    /// <summary>Строка в общий журнал: при автозапуске окна нет.</summary>
    /// <remarks>
    /// С датой, а не только со временем. Журнал общий, и супервизор пишет
    /// в него полную дату; строки автозапуска без неё невозможно отнести
    /// к дню — при разборе 19.09 пришлось искать их по соседним строкам,
    /// чтобы узнать, сегодняшние они или недельной давности.
    /// </remarks>
    private static void Note(string message)
    {
        try
        {
            using var log = Supervisor.SharedLogWriter.TryOpen(
                Path.Combine("runtime", "supervisor.log"));

            log?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
        }
        catch (Exception)
        {
            // Потеря строки журнала не должна ронять то, о чём она.
        }
    }

    /// <summary>Выход по-настоящему, а не прятки в трей.</summary>
    /// <remarks>
    /// Крестик отменяет закрытие и прячет окно; без этого признака выход
    /// из меню значка отменялся бы тем же обработчиком, и программа
    /// не закрывалась бы вовсе.
    /// </remarks>
    internal static bool Exiting;

    private TrayIcon? _tray;

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        SingleInstance.Release();

        base.OnExit(e);
    }

    /// <summary>
    /// Поднимает движки без окна — ровно как кнопка «Запустить».
    /// </summary>
    /// <remarks>
    /// Через <see cref="EngineControl"/>, как и кнопка. Свой запуск
    /// процессов отличался бы рабочим каталогом, правами или ключами,
    /// и разбор вёлся бы не над тем, что у человека.
    /// </remarks>
    private async void RunStart() => Shutdown(await Headless.StartAsync(CancellationToken.None));

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

    /// <remarks>
    /// Не нашли — оставляем как есть. Разделы скажут об этом сами, каждый
    /// про своё: так понятнее, чем одно окно с ошибкой при запуске.
    /// Признак установки — см. <see cref="InstallRoot"/>: прежний, папка
    /// config, срабатывал в System32 и обрывал поиск (issue #3).
    /// </remarks>
    private static void MoveToInstallDirectory() => InstallRoot.MoveTo();
}
