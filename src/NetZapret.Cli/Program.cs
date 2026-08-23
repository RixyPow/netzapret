using NetZapret.Cli.Commands;
using NetZapret.Core.Rules;
using NetZapret.Wfp;

namespace NetZapret.Cli;

internal static class Program
{
    private const string DefaultConfigPath = "config/rules.yaml";

    private static async Task<int> Main(string[] args)
    {
        var cmd = CommandLine.Parse(args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return cmd.Command switch
            {
                "watch" => await WatchCommand.RunAsync(cmd, DefaultConfigPath, cts.Token),
                "rules" => RulesCommand.Run(cmd, DefaultConfigPath),
                "test" => TestCommand.Run(cmd, DefaultConfigPath),
                "sub" => await SubCommand.RunAsync(cmd, cts.Token),
                "config" => await ConfigCommand.RunAsync(cmd, DefaultConfigPath, cts.Token),
                "preset" => PresetCommand.Run(cmd),
                "probe" => await ProbeCommand.RunAsync(cmd, cts.Token),
                "status" => StatusCommand.Run(),
                "help" or "--help" or "-h" => PrintUsage(0),
                _ => PrintUnknown(cmd.Command),
            };
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
              watch    Наблюдать за соединениями и показывать применяемые правила
              rules    Показать разобранный набор правил в порядке вычисления
              test     Прогнать синтетическое соединение через правила
              sub      Показать содержимое подписки: квоту, срок, серверы
              config   Сгенерировать конфиг sing-box из правил и подписки
              preset   Показать пресеты Zapret и их покрытие десинком
              probe    Проверить, идёт ли трафик через серверы подписки
              status   Состояние подключения к прокси-серверу
              help     Эта справка

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

              Сгенерированный конфиг сразу проверяется командой sing-box check,
              если движок найден в tools/. Права администратора для этого не нужны.

            preset
              --zapret-root <путь> Корень установки Zapret (ищется сам)
              --preset <название>  Разобрать конкретный пресет
              --verbose            Показать все секции с рецептами

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
