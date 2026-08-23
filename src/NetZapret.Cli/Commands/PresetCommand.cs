using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Показывает пресеты Zapret и то, что они покрывают десинком.
/// </summary>
internal static class PresetCommand
{
    public static int Run(CommandLine cmd)
    {
        var paths = ZapretPaths.Discover(cmd.Value("zapret-root"));

        if (paths is null)
        {
            Console.Error.WriteLine(
                "Установка Zapret не найдена. Укажите --zapret-root <путь к каталогу с presets\\winws2>.");
            return 2;
        }

        var reader = new PresetReader();
        var name = cmd.Value("preset");

        if (name is null)
        {
            ListPresets(reader, paths);
            return 0;
        }

        var path = ResolvePreset(paths, name);

        if (path is null)
        {
            Console.Error.WriteLine($"Пресет '{name}' не найден в {paths.PresetDirectory}.");
            return 2;
        }

        ShowPreset(reader, paths, path, cmd.Has("verbose"));
        return 0;
    }

    private static void ListPresets(PresetReader reader, ZapretPaths paths)
    {
        var presets = reader.List(paths.PresetDirectory);

        Console.WriteLine($"Каталог: {paths.PresetDirectory}");
        Console.WriteLine($"Пресетов: {presets.Count}");
        Console.WriteLine();
        Console.WriteLine($"  {"НАЗВАНИЕ",-32} {"ВЕРСИЯ",-8} {"СЕКЦИЙ",-8} {"ДЕСИНК",-8} PASS");
        Console.WriteLine("  " + new string('-', 74));

        foreach (var preset in presets)
        {
            int active = preset.ActiveSections.Count();
            int pass = preset.Sections.Count(s => s.IsPassThrough);

            Console.WriteLine(
                $"  {Truncate(preset.Name, 32),-32} {preset.BuiltinVersion ?? "-",-8} " +
                $"{preset.Sections.Count,-8} {active,-8} {pass}");
        }

        Console.WriteLine();
        Console.WriteLine("Подробности: netzapret preset --preset \"Universal V6\"");
    }

    private static void ShowPreset(PresetReader reader, ZapretPaths paths, string path, bool verbose)
    {
        var preset = reader.Load(path);

        Console.WriteLine($"Пресет:   {preset.Name}");
        Console.WriteLine($"Файл:     {preset.FilePath}");

        if (preset.BuiltinVersion is not null)
            Console.WriteLine($"Версия:   {preset.BuiltinVersion}");

        if (preset.Description is not null)
            Console.WriteLine($"Описание: {Truncate(preset.Description, 120)}");

        Console.WriteLine($"Секций:   {preset.Sections.Count}, из них с десинком {preset.ActiveSections.Count()}");
        Console.WriteLine($"Глобальных ключей: {preset.GlobalArguments.Count}");

        var domains = reader.CollectCoveredDomains(preset, paths.Root);
        var addresses = reader.CollectCoveredAddresses(preset, paths.Root);

        Console.WriteLine();
        Console.WriteLine($"Покрыто десинком: {domains.Count} доменов, {addresses.Count} подсетей.");
        Console.WriteLine("Подсети — готовый список для route_exclude_address у TUN.");

        var fakeSections = preset.ActiveSections.Where(s => s.UsesFakePackets).ToList();
        if (fakeSections.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Секций с поддельными пакетами: {fakeSections.Count}. Именно они ломаются " +
                "под поднятым TUN, если трафик не выведен из туннеля.");
        }

        if (!verbose)
        {
            Console.WriteLine();
            Console.WriteLine("Полный разбор секций: добавьте --verbose");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"СЕКЦИЯ",-38} {"СПИСКИ",-10} {"FAKE",-6} РЕЦЕПТ");
        Console.WriteLine("  " + new string('-', 100));

        foreach (var section in preset.Sections)
        {
            var lists = section.HostListPaths.Count + section.IpSetPaths.Count + section.InlineDomains.Count;
            var recipe = section.IsPassThrough
                ? "pass (не трогать)"
                : string.Join(" + ", section.DesyncRecipes.Select(HeadOf));

            var previous = Console.ForegroundColor;
            if (section.IsPassThrough)
                Console.ForegroundColor = ConsoleColor.DarkGray;
            else if (section.UsesFakePackets)
                Console.ForegroundColor = ConsoleColor.Yellow;

            Console.WriteLine(
                $"  {Truncate(section.Name, 38),-38} {lists,-10} " +
                $"{(section.UsesFakePackets ? "да" : ""),-6} {Truncate(recipe, 40)}");

            Console.ForegroundColor = previous;
        }
    }

    private static string? ResolvePreset(ZapretPaths paths, string name)
    {
        var direct = Path.Combine(paths.PresetDirectory, name.EndsWith(".txt") ? name : name + ".txt");
        if (File.Exists(direct))
            return direct;

        return Directory
            .EnumerateFiles(paths.PresetDirectory, "*.txt")
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                .Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string HeadOf(string recipe)
    {
        int colon = recipe.IndexOf(':');
        return colon < 0 ? recipe : recipe[..colon];
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
