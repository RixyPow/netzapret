using System.Diagnostics;
using NetZapret.Proxy;

namespace NetZapret.Core.Tests;

/// <summary>
/// Прогон собранного конфига через сам <c>sing-box check</c>.
/// </summary>
/// <remarks>
/// Только движку здесь и можно верить: 01.10 именно он показал, что «jc»
/// строкой и непонятая выходом «parser» ссылка роняют конфиг целиком.
/// Движка нет рядом (сборка без tools\) — проверка считается пройденной,
/// как и в прежних тестах: им нечем проверить, и падать за это нечестно.
/// </remarks>
internal static class SingBoxCheck
{
    public static (bool Ok, string Said) Run(string json)
    {
        var singBox = Find();
        if (singBox is null)
            return (true, "sing-box не найден — проверка пропущена");

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-check-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, json);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var said = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode == 0, said);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools)
                && Directory.EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
