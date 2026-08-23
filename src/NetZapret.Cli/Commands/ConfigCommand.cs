using System.Diagnostics;
using NetZapret.Core.Rules;
using NetZapret.Proxy;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Генерирует конфиг sing-box из правил и подписки.
/// </summary>
internal static class ConfigCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, string defaultConfigPath, CancellationToken cancellationToken)
    {
        var configPath = cmd.Value("config", defaultConfigPath);
        var engine = RuleSetLoader.LoadFromFile(configPath);

        var info = await SubscriptionSource.LoadAsync(cmd, cancellationToken);
        if (info is null)
            return 2;

        var compiler = new SingBoxConfigCompiler();
        var options = new SingBoxOptions
        {
            UseTun = !cmd.Has("no-tun"),
            LocalListenPort = cmd.Int("local-port", 21080),
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

    /// <summary>Ищет sing-box.exe в tools/, не завися от точного имени каталога с версией.</summary>
    private static string? FindSingBox()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools"),
            @"C:\Users\rogfa\Projects\netzapret\tools",
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            var found = Directory
                .EnumerateFiles(root, "sing-box.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (found is not null)
                return found;
        }

        return null;
    }
}
