using NetZapret.Cli.Commands;
using NetZapret.Core.Rules;
using NetZapret.Wfp;

namespace NetZapret.Cli;

internal static class Program
{
    private const string DefaultConfigPath = "config/rules.yaml";

    private static async Task<int> Main(string[] args)
    {
        // Двойной щелчок по программе — обычный способ её запустить, и он
        // должен открывать меню. Раньше без аргументов печаталась справка
        // об ошибке, окно моргало и закрывалось, а запускать полагалось
        // соседний .cmd — то есть из двух файлов рядом правильным был
        // менее очевидный.
        MoveToConfigDirectory();

        if (args.Length == 0)
            return await StartMenuAsync();

        var cmd = CommandLine.Parse(args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var code = await DispatchAsync(cmd, cts.Token);

            WarnAboutUnusedOptions(cmd);
            return code;
        }
        catch (RuleConfigurationException ex)
        {
            Console.Error.WriteLine($"Ошибка конфига: {ex.Message}");
            return 2;
        }
        catch (WfpException ex)
        {
            Console.Error.WriteLine($"Ошибка WFP: {ex.Message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Сообщает об опциях, которых команда не спрашивала.
    /// </summary>
    /// <remarks>
    /// Молча отброшенная опция означает, что команда сделала не то, о чём
    /// её просили, ничем этого не выдав. Предупреждение, а не отказ: команда
    /// уже отработала, и обрывать её из-за опечатки в необязательном ключе
    /// было бы хуже самой опечатки.
    /// </remarks>
    private static void WarnAboutUnusedOptions(CommandLine cmd)
    {
        var unused = cmd.UnusedOptions();

        if (unused.Count == 0)
            return;

        Console.Error.WriteLine();

        foreach (var key in unused)
            Console.Error.WriteLine($"Внимание: команда '{cmd.Command}' не знает опции --{key} и не учла её.");

        Console.Error.WriteLine($"Что она понимает: netzapret help");
    }

    private static async Task<int> DispatchAsync(CommandLine cmd, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        return cmd.Command switch
        {
                "watch" => await WatchCommand.RunAsync(cmd, DefaultConfigPath, cts.Token),
                "rules" => RulesCommand.Run(cmd, DefaultConfigPath),
                "test" => TestCommand.Run(cmd, DefaultConfigPath),
                "where" => WhereCommand.Run(cmd, DefaultConfigPath),
                "route" => RouteCommand.Run(cmd),
                "sub" => await SubCommand.RunAsync(cmd, cts.Token),
                "config" => await ConfigCommand.RunAsync(cmd, DefaultConfigPath, cts.Token),
                "preset" => PresetCommand.Run(cmd),
                "dns" => await DnsCommand.RunAsync(cmd, cts.Token),
                "blockcheck" => await BlockCheckCommand.RunAsync(cmd, DefaultConfigPath, cts.Token),
                "probe" => await ProbeCommand.RunAsync(cmd, cts.Token),
                "start" => await SupervisorCommands.StartAsync(cmd, cts.Token),
                "stop" => await SupervisorCommands.StopAsync(cmd, cts.Token),
                "status" => SupervisorCommands.Status(cmd),
                "doctor" => DoctorCommand.Run(cmd),
                "clean" => CleanCommand.Run(cmd),
                "autostart" => AutostartCommand.Run(cmd),
            "menu" => await RunMenuAsync(cmd, cts.Token),
            "help" or "--help" or "-h" => PrintUsage(0),
            _ => PrintUnknown(cmd.Command),
        };
    }

    /// <summary>
    /// Переходит в каталог, где лежит <c>config/</c>, если его нет в текущем.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пути к правилам, настройкам и <c>runtime/</c> заданы относительно
    /// рабочего каталога. При запуске из проводника им становится папка
    /// с программой, и в рабочей копии это не то место: там программа лежит
    /// в <c>build\</c>, а <c>config\</c> — уровнем выше. Меню открывалось
    /// с пустыми настройками, будто его запустили впервые.
    /// </para>
    /// <para>
    /// Поиск вверх покрывает обе раскладки: в дистрибутиве <c>config\</c>
    /// лежит рядом с программой, в рабочей копии — на уровень выше.
    /// </para>
    /// <para>
    /// Только если в текущем каталоге его нет: запуск из своего каталога
    /// со своим набором правил — законный случай, и уводить из него нельзя.
    /// </para>
    /// </remarks>
    private static void MoveToConfigDirectory()
    {
        try
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
        }
        catch (Exception)
        {
            // Не вышло — остаёмся где были: команда сама скажет,
            // чего ей не хватает.
        }
    }

    /// <summary>
    /// Запуск двойным щелчком: поднимает права и открывает меню.
    /// </summary>
    /// <remarks>
    /// Права запрашиваются здесь, а не по месту нужды: TUN и драйвер WinDivert
    /// требуют администратора, и без него запуск проваливается уже на середине,
    /// когда часть работы сделана. Один запрос в начале понятнее.
    /// </remarks>
    private static async Task<int> StartMenuAsync()
    {
        if (!IsElevated())
        {
            try
            {
                var self = Environment.ProcessPath;

                if (self is null)
                {
                    Console.Error.WriteLine("Не удалось определить путь к программе.");
                    return 1;
                }

                using var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = self,
                    Arguments = "menu",
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                });

                return 0;
            }
            catch (Exception)
            {
                // Отказ в запросе прав — не ошибка программы, а решение человека.
                // Продолжаем без них: меню работает, просто движки не поднимутся,
                // и оно об этом скажет.
            }
        }

        using var cts = new CancellationTokenSource();
        return await RunMenuAsync(CommandLine.Parse(["menu"]), cts.Token);
    }

    private static async Task<int> RunMenuAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        UseUtf8();
        return await MenuCommand.RunAsync(cmd, cancellationToken);
    }

    /// <summary>
    /// Переключает вывод на UTF-8, иначе кириллица в меню превращается в мусор.
    /// </summary>
    /// <remarks>
    /// Здесь, а не через <c>chcp</c> в обёртке: программа теперь запускается
    /// и напрямую, и обёртки может не быть вовсе.
    /// </remarks>
    private static void UseUtf8()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception)
        {
            // Консоли может не быть — при перенаправленном выводе, например.
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static int PrintUnknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        Console.Error.WriteLine();
        PrintUsage(1);
        return 1;
    }

    private static int PrintUsage(int exitCode)
    {
        Console.WriteLine("""
            netzapret — диспетчер маршрутизации трафика

            КОМАНДЫ
              menu       Интерактивное меню: режим, пресет, сервер, запуск
              watch      Наблюдать за соединениями и показывать применяемые правила
              rules      Показать разобранный набор правил в порядке вычисления
              where      Куда пойдёт приложение или сайт: напрямую, десинк или VPN
              route      Задать свой маршрут для приложения или сайта
              test       Прогнать синтетическое соединение через правила
              sub        Показать содержимое подписки: квоту, срок, серверы
              config     Сгенерировать конфиг sing-box из правил и подписки
              preset     Показать пресеты Zapret и их покрытие десинком
              dns        Проверить резолверы и выбрать апстрим
              blockcheck Измерить, что закрыто в этой сети и чем это лечится
              probe      Проверить, идёт ли трафик через серверы подписки
              start      Запустить службы под присмотром супервизора
              stop       Остановить супервизор и все службы
              status     Состояние супервизора и служб
              doctor     Обзор: что найдено и что готово к работе
              clean      Убрать рабочие файлы и логи из runtime
              autostart  Управление автозапуском: install / remove / status
              help       Эта справка

            ОБЩИЕ ОПЦИИ
              --config <путь>      Файл правил (по умолчанию config/rules.yaml)

            watch
              --source <etw|wfp>   Источник событий (по умолчанию etw)
              --hosts-map <путь>   Карта «адрес имя» для проверки доменных правил
              --stats-interval <с> Период вывода счётчиков (по умолчанию 15, 0 — выключить)
              --all                Показывать и loopback-соединения
              --udp-window <с>     Окно подавления повторов UDP для etw (по умолчанию 15)
              --duration <с>       Остановиться самому через N секунд
              --no-dns             Не подсматривать имена в ответах DNS
              --dump-raw <N>       Шестнадцатеричный дамп первых N событий, только для wfp

              Требует прав администратора в обоих режимах. Источник wfp вдобавок
              включает общесистемный сбор net-events; исходные настройки
              восстанавливаются при выходе, поэтому завершать нужно через Ctrl+C,
              а не убийством процесса.

            test
              --process <exe>      Имя или путь исполняемого файла
              --domain <имя>       Доменное имя назначения
              --ip <адрес>         Адрес назначения
              --port <порт>        Порт назначения (по умолчанию 443)

            sub / config
              --sub <ссылка|файл>  Подписка: URL либо файл с её телом
              --out <путь>         Куда писать конфиг (по умолчанию runtime/singbox.json)
              --no-tun             Локальный прокси вместо TUN: без прав администратора
              --local-port <порт>  Порт локального прокси для --no-tun (21080)
              --proxy-only         В туннель заводить только трафик прокси,
                                   чтобы десинк работал на исходных потоках
              --exclude-preset     Вывести из туннеля подсети пресета Zapret;
                                   нужно при сплошном перехвате, при --proxy-only избыточно
              --preset <название>  Какой пресет читать для --exclude-preset

              Сгенерированный конфиг сразу проверяется командой sing-box check,
              если движок найден в tools/. Права администратора для этого не нужны.

            preset
              --zapret-root <путь> Корень установки Zapret (ищется сам)
              --preset <название>  Разобрать конкретный пресет
              --verbose            Показать все секции с рецептами

            blockcheck
              --quick              По имени с каждой части (по умолчанию)
              --full               По два имени: дольше, зато подробнее
              --service <имя>      Только один сервис, зато целиком
              --zapret-root <путь> Корень установки Zapret (ищется сам)

              Пробы идут разом, Esc прерывает и показывает набранное.

              Проверять надо при остановленных движках: с включённым обходом
              видно сеть уже вылеченной, а не такой, какая она есть.

            start / stop / status
              --proxy-config <путь>  Конфиг sing-box (по умолчанию runtime/singbox.json)
              --clash-port <порт>    Порт Clash API для проверки живости (9090)
              --preset <название>    Запускать ещё и winws2 с этим пресетом
              --no-proxy             Не запускать sing-box
              --max-restarts <N>     Сколько раз перезапускать упавшую службу (5)
              --check-interval <с>   Период опроса служб (5)
              --verify-traffic       Проверять не только порт, но и проход трафика
              --local-port <порт>    Порт локального прокси для --verify-traffic (21080)

              start работает в переднем плане, Ctrl+C останавливает всё.
              Запуск winws2 требует прав администратора.

            autostart <install|remove|status>
              --preset <название>    Какой пресет запускать при входе
              --proxy-config <путь>  Конфиг прокси; сохраняется абсолютным
              --work-dir <путь>      Рабочий каталог задачи (текущий по умолчанию)
              --dry-run              Показать параметры задачи, не создавая её

              Автозапуск сделан задачей планировщика с наивысшими привилегиями:
              так права администратора выдаются без запроса UAC при входе.
              install и remove требуют консоли администратора.

            where / route
              Правила лежат в двух слоях. Базовый config/rules.yaml мы ведём
              сами: там то, что заблокировано наверняка. Ваши решения пишутся
              в config/rules.user.yaml и проверяются раньше базовых.

              nz where <что>              куда пойдёт и почему
              nz route                    список ваших правил
              nz route <что> vpn          через VPN
              nz route <что> десинк       через Zapret
              nz route <что> напрямую     не трогать
              nz route off <что>          убрать, вернуть базовую настройку

            ПРИМЕРЫ
              netzapret rules --config config/rules.example.yaml
              netzapret test --domain rutracker.org --ip 1.2.3.4
              netzapret watch --config config/rules.example.yaml --duration 30
              netzapret sub --sub https://example.com/sub/token
              netzapret config --sub https://example.com/sub/token --out runtime/singbox.json
            """);

        return exitCode;
    }
}
