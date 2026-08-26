using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Проверяет резолверы и позволяет выбрать апстрим.
/// </summary>
/// <remarks>
/// Отдельной командой, а не только пунктом меню: вопрос «какой DNS сейчас
/// работает» стал будничным с тех пор, как операторы начали перекрывать DoH,
/// и отвечать на него удобнее одной строкой.
/// </remarks>
internal static class DnsCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var settingsPath = cmd.Value("settings", AppSettings.DefaultPath);
        var settings = AppSettings.Load(settingsPath);

        var chosen = cmd.Value("set");

        if (chosen is not null)
            return Set(settings, settingsPath, chosen);

        WarnIfTunnelUp(settings);

        Console.WriteLine();
        Console.WriteLine($"Сейчас: {settings.DnsServer}{(settings.DnsThroughTunnel ? " (через туннель)" : string.Empty)}");
        Console.WriteLine();
        Console.WriteLine($"  {"РЕЗОЛВЕР",-14} {"АДРЕС",-18} {"ОТКЛИК",-12} ПРИМЕЧАНИЕ");
        Console.WriteLine("  " + new string('-', 74));

        var timeout = TimeSpan.FromSeconds(cmd.Int("timeout", 5));

        var results = await DnsProbe.CheckAllAsync(
            DnsProbe.Known,
            timeout,
            r => Print(r, settings.DnsServer),
            cancellationToken);

        var working = results.Count(r => r.Works);

        Console.WriteLine();
        Console.WriteLine($"Отвечают: {working} из {results.Count}");

        if (working == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Ни один не ответил напрямую. Если туннель поднят — включите");
            Console.WriteLine("DnsThroughTunnel в config/netzapret.json: внутри туннеля запрос");
            Console.WriteLine("оператору не виден.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Выбрать:  netzapret dns --set 1.1.1.1");

        return 0;
    }

    /// <summary>
    /// Предупреждает, что при поднятом туннеле замер говорит не о том.
    /// </summary>
    /// <remarks>
    /// Мы сами закрываем DoH к системным резолверам внутри туннеля, чтобы
    /// Windows вернулась на обычный UDP. Наш собственный запрос попадает
    /// под то же правило, и резолвер покажется недоступным, хотя снаружи
    /// отвечает.
    /// </remarks>
    private static void WarnIfTunnelUp(AppSettings settings)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("Туннель поднят — замер может занизить доступность: мы сами");
        Console.WriteLine("закрываем DoH к системным резолверам, и наш запрос попадает");
        Console.WriteLine("под то же правило. Для чистой картины остановите движки.");
        Console.ForegroundColor = previous;
    }

    private static void Print(DnsProbeResult result, string current)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = result.Works ? ConsoleColor.Green : ConsoleColor.Red;

        var latency = result.Works && result.Latency is { } l
            ? $"{l.TotalMilliseconds,5:0} мс"
            : "не отвечает";

        var mark = string.Equals(result.Resolver.Address, current, StringComparison.Ordinal)
            ? " <- сейчас"
            : string.Empty;

        Console.WriteLine(
            $"  {result.Resolver.Name,-14} {result.Resolver.Address,-18} {latency,-12} " +
            $"{result.Resolver.Note}{mark}");

        Console.ForegroundColor = previous;

        if (!result.Works && result.Error is not null)
            Console.WriteLine($"                 {result.Error}");
    }

    private static int Set(AppSettings settings, string path, string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            Console.Error.WriteLine($"'{address}' не разбирается как адрес. Резолвер задаётся адресом, не именем.");
            return 2;
        }

        (settings with { DnsServer = address }).Save(path);

        Console.WriteLine($"Апстрим DNS: {address}");
        Console.WriteLine("Применится при следующем запуске.");

        return 0;
    }
}
