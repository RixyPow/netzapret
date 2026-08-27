using System.Net;
using NetZapret.Core.Rules;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Правит пользовательский слой правил.
/// </summary>
/// <remarks>
/// Базовый набор не трогается: он обновляется вместе с программой,
/// и правки в нём затирались бы.
/// </remarks>
internal static class RouteCommand
{
    public static int Run(CommandLine cmd)
    {
        var file = UserRulesFile.Load(cmd.Value("user-rules", UserRulesFile.DefaultPath));

        if (cmd.Positional.Count == 0)
            return List(file);

        var target = cmd.Positional[0];

        if (string.Equals(target, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "remove", StringComparison.OrdinalIgnoreCase))
        {
            return Remove(cmd, file);
        }

        if (cmd.Positional.Count < 2)
        {
            Console.Error.WriteLine("Укажите, куда направить: напрямую, десинк или vpn.");
            Console.Error.WriteLine("  nz route rutracker.org vpn");
            return 2;
        }

        var mode = ParseMode(cmd.Positional[1]);

        if (mode is null)
        {
            Console.Error.WriteLine(
                $"Не понимаю '{cmd.Positional[1]}'. Допустимо: напрямую, десинк, vpn.");
            return 2;
        }

        var match = Detect(target);

        file.Set(match, target, mode.Value);
        file.Save();

        Console.WriteLine($"{target} → {Describe(mode.Value)}");
        Console.WriteLine($"Записано в {file.Path}");

        PrintCaveat(match, mode.Value);
        PrintNextStep();

        return 0;
    }

    private static int Remove(CommandLine cmd, UserRulesFile file)
    {
        if (cmd.Positional.Count < 2)
        {
            Console.Error.WriteLine("Укажите, что убрать: nz route off rutracker.org");
            return 2;
        }

        var target = cmd.Positional[1];

        if (!file.Remove(Detect(target), target))
        {
            Console.WriteLine($"Записи про {target} в ваших правилах нет.");
            return 1;
        }

        file.Save();

        Console.WriteLine($"Убрано: {target}. Теперь действует базовая настройка.");
        PrintNextStep();

        return 0;
    }

    private static int List(UserRulesFile file)
    {
        if (file.Entries.Count == 0)
        {
            Console.WriteLine("Ваших правил пока нет — действует базовый набор.");
            Console.WriteLine();
            Console.WriteLine("Добавить:  nz route rutracker.org vpn");
            Console.WriteLine("Посмотреть путь:  nz where rutracker.org");
            return 0;
        }

        Console.WriteLine($"Ваши правила ({file.Path}):");
        Console.WriteLine();
        Console.WriteLine($"  {"#",-4} {"ТИП",-11} {"ЧТО",-34} {"КУДА",-11} СОСТОЯНИЕ");
        Console.WriteLine("  " + new string('-', 76));

        for (int i = 0; i < file.Entries.Count; i++)
        {
            var entry = file.Entries[i];
            var previous = Console.ForegroundColor;

            if (!entry.Enabled)
                Console.ForegroundColor = ConsoleColor.DarkGray;

            Console.WriteLine(
                $"  {i + 1,-4} {entry.DescribeMatch(),-11} {Truncate(entry.Value, 34),-34} " +
                $"{entry.DescribeMode(),-11} {(entry.Enabled ? "включено" : "выключено")}");

            Console.ForegroundColor = previous;
        }

        Console.WriteLine();
        Console.WriteLine("Убрать:  nz route off <что>");

        return 0;
    }

    /// <summary>
    /// Определяет, чем является введённое: программой, адресом или доменом.
    /// </summary>
    /// <remarks>
    /// Порядок проверок значим: <c>1.2.3.4</c> должно разбираться как адрес
    /// раньше, чем как домен, а <c>discord.exe</c> — как программа, хотя
    /// внешне похоже на доменное имя.
    /// </remarks>
    public static MatchKind Detect(string target)
    {
        var value = target.Trim();

        // Список может быть и адресным, и доменным, а по имени файла они
        // неразличимы: и lists/ipset-steam.txt, и lists/steam.txt — просто
        // .txt. Поэтому заглядываем внутрь. Пока не заглядывали, доменный
        // список записывался как ipset, разворачивался в пустоту и правило
        // не совпадало никогда — молча, без единой жалобы.
        if (value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return LooksLikeAddresses(value) ? MatchKind.IpSet : MatchKind.HostList;

        // Подсеть уходит в ipset, а не в ip, и это не описка: значение
        // такого правила разворачивает AddressListReader, который принимает
        // и готовый CIDR, и путь к списку. Одна ветка вместо двух.
        if (value.Contains('/'))
            return MatchKind.IpSet;

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.Contains('\\'))
            return MatchKind.Process;

        if (IPAddress.TryParse(value, out _))
            return MatchKind.Ip;

        return MatchKind.Domain;
    }

    /// <summary>
    /// Адресный ли это список.
    /// </summary>
    /// <remarks>
    /// Решает первая содержательная строка. Смешанных списков не бывает:
    /// winws2 получает их разными ключами — <c>--ipset</c> и <c>--hostlist</c>, —
    /// и файл, годный для обоих, был бы негоден ни для одного.
    /// Нечитаемый файл считается адресным: так было раньше, и менять
    /// поведение для случая, который всё равно кончится жалобой на
    /// ненайденный список, незачем.
    /// </remarks>
    private static bool LooksLikeAddresses(string path)
    {
        var full = ZapretPaths.Discover()?.Root is { } root && File.Exists(Path.Combine(root, path))
            ? Path.Combine(root, path)
            : path;

        try
        {
            foreach (var raw in File.ReadLines(full))
            {
                var line = raw.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var head = line.Split('/')[0];

                return IPAddress.TryParse(head, out _);
            }
        }
        catch (Exception)
        {
        }

        return true;
    }

    public static RoutingMode? ParseMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "vpn" or "proxy" or "впн" or "прокси" => RoutingMode.Proxy,
            "desync" or "десинк" or "zapret" or "запрет" => RoutingMode.Desync,
            "direct" or "напрямую" or "прямо" or "direct-only" => RoutingMode.Direct,
            _ => null,
        };

    public static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк (Zapret)",
        _ => "напрямую",
    };

    /// <summary>
    /// Предупреждает о случаях, когда правило верное, но не сработает.
    /// </summary>
    private static void PrintCaveat(MatchKind match, RoutingMode mode)
    {
        if (mode != RoutingMode.Proxy || match != MatchKind.Process)
            return;

        Console.WriteLine();
        Console.WriteLine("Учтите: перехват решается по адресу назначения, а имя программы");
        Console.WriteLine("проверяется уже внутри туннеля. Если приложение ходит по голым");
        Console.WriteLine("адресам без DNS, добавьте его подсети в секцию capture базового");
        Console.WriteLine("набора — иначе правило верное, но не сработает никогда.");
    }

    private static void PrintNextStep()
    {
        Console.WriteLine();
        Console.WriteLine("Чтобы применить: пересоберите конфиг и перезапустите.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
