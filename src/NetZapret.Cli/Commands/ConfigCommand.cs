using System.Diagnostics;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Генерирует конфиг sing-box из правил и подписки.
/// </summary>
internal static class ConfigCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, string defaultConfigPath, CancellationToken cancellationToken)
    {
        var configPath = cmd.Value("config", defaultConfigPath);
        var engine = RuleSetLoader.LoadLayered(
            configPath,
            cmd.Value("user-rules", UserRulesFile.DefaultPath));

        var info = await SubscriptionSource.LoadAsync(cmd, cancellationToken);
        if (info is null)
            return 2;

        bool proxyOnly = cmd.Has("proxy-only");

        // Исключения по спискам пресета имеют смысл только при сплошном
        // перехвате. При выборочном в туннель и так заходит лишь fakeip,
        // а установка семи тысяч маршрутов ради страховки того не стоит.
        var desyncAddresses = cmd.Has("exclude-preset")
            ? LoadPresetAddresses(cmd)
            : Array.Empty<string>();

        var zapretRoot = ZapretPaths.Discover(cmd.Value("zapret-root"))?.Root;
        var capture = AddressListReader.Expand(engine.RuleSet.CaptureEntries, zapretRoot, out var captureProblems);

        foreach (var problem in captureProblems)
            Console.WriteLine($"Внимание, секция capture — {problem}");

        foreach (var problem in RuleSetExpander.Expand(engine.RuleSet, zapretRoot))
            Console.WriteLine($"Внимание: {problem}");

        // hosts бьёт любой резолв, включая наш: прибитый там домен не получит
        // fakeip и уйдёт мимо туннеля, сколько бы правил на него ни стояло.
        var pinned = HostsFile.CollectPinnedProxyAddresses(engine.RuleSet, out var pinnedNotes);

        foreach (var note in pinnedNotes)
            Console.WriteLine($"Внимание, hosts: {note}");

        var compiler = new SingBoxConfigCompiler();
        var options = new SingBoxOptions
        {
            UseTun = !cmd.Has("no-tun"),
            LocalListenPort = cmd.Int("local-port", 21080),
            Scope = proxyOnly ? TunnelScope.ProxyOnly : TunnelScope.Everything,
            DnsServerAddresses = proxyOnly ? SystemResolvers.Discover() : Array.Empty<string>(),
            DesyncAddresses = desyncAddresses,
            CaptureAddresses = [.. capture, .. pinned],
        };

        var result = compiler.Compile(engine.RuleSet, info.Servers, options);

        var outputPath = cmd.Value("out", "runtime/singbox.json");
        SingBoxConfigCompiler.WriteToFile(outputPath, result.Json);

        Console.WriteLine();
        Console.WriteLine($"Правила:  {configPath} (режим {engine.RuleSet.Operating.ToString().ToLowerInvariant()})");
        Console.WriteLine($"Серверов: {result.UsedServers.Count} использовано, {result.SkippedServers.Count} пропущено");
        Console.WriteLine($"Конфиг:   {Path.GetFullPath(outputPath)}");
        Console.WriteLine(options.UseTun
            ? "Режим:    TUN (запуск требует прав администратора)"
            : $"Режим:    локальный прокси на 127.0.0.1:{options.LocalListenPort} (прав администратора не нужно)");

        if (options.Scope == TunnelScope.ProxyOnly)
        {
            Console.WriteLine(
                $"Перехват: выборочный — в туннель заходит только трафик прокси " +
                $"(fakeip + {options.DnsServerAddresses.Count} резолвер(ов))");
        }

        if (options.DesyncAddresses.Count > 0)
            Console.WriteLine($"Исключено из туннеля: {options.DesyncAddresses.Count} подсетей из пресета");

        if (options.CaptureAddresses.Count > 0)
            Console.WriteLine($"Заведено в туннель дополнительно: {options.CaptureAddresses.Count} подсетей из секции capture");

        if (result.UsedServers.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "Ни одного пригодного сервера: правила с mode: proxy скомпилированы в direct, " +
                "иначе конфиг ссылался бы на несуществующий outbound.");
        }

        return Validate(outputPath);
    }

    /// <summary>
    /// Достаёт из пресета Zapret подсети, покрытые десинком.
    /// </summary>
    private static IReadOnlyList<string> LoadPresetAddresses(CommandLine cmd)
    {
        var paths = ZapretPaths.Discover(cmd.Value("zapret-root"));

        if (paths is null)
        {
            Console.WriteLine("Установка Zapret не найдена — исключения по спискам пресета не добавлены.");
            return Array.Empty<string>();
        }

        var presetName = cmd.Value("preset");
        var reader = new PresetReader();

        var presetPath = presetName is null
            ? null
            : Directory.EnumerateFiles(paths.PresetDirectory, "*.txt")
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                    .Contains(presetName, StringComparison.OrdinalIgnoreCase));

        if (presetPath is null)
        {
            if (presetName is not null)
                Console.WriteLine($"Пресет '{presetName}' не найден — исключения не добавлены.");

            return Array.Empty<string>();
        }

        return reader.CollectCoveredAddresses(reader.Load(presetPath), paths.Root);
    }

    /// <summary>
    /// Проверяет конфиг штатной командой sing-box.
    /// </summary>
    /// <remarks>
    /// <c>check</c> разбирает конфиг, не запуская его и не требуя прав
    /// администратора, — поэтому генератор можно проверять на каждом прогоне.
    /// </remarks>
    private static int Validate(string configPath)
    {
        var singBox = FindSingBox();

        if (singBox is null)
        {
            Console.WriteLine();
            Console.WriteLine("sing-box.exe не найден в tools/ — конфиг не проверен.");
            return 0;
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = singBox,
            ArgumentList = { "check", "-c", Path.GetFullPath(configPath) },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            Console.Error.WriteLine("Не удалось запустить sing-box для проверки.");
            return 1;
        }

        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Console.WriteLine();

        if (process.ExitCode == 0)
        {
            Console.WriteLine("Проверка sing-box: конфиг корректен.");
            return 0;
        }

        Console.Error.WriteLine($"Проверка sing-box не пройдена (код {process.ExitCode}):");
        if (stderr.Length > 0)
            Console.Error.WriteLine(stderr.Trim());
        if (stdout.Length > 0)
            Console.Error.WriteLine(stdout.Trim());

        return 1;
    }

    // Свой поиск движка здесь был лишним и вдобавок содержал абсолютный путь
    // к моему каталогу проекта — у всех остальных он не значит ничего.
    // Единственный поиск живёт в EngineLocator.
    private static string? FindSingBox() => EngineLocator.FindSingBox();
}
