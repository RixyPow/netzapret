using Android.Content;
using Android.Content.Res;
using NetZapret.Core.Updates;

namespace NetZapret.Android.Startup;

/// <summary>
/// Распаковка поставки (assets/payload/ в APK) в папку данных приложения.
/// </summary>
/// <remarks>
/// <para>
/// Библиотеки src/ читают config/, presets/ и engines/zapret/ по путям от
/// текущей папки и от <c>AppPaths.ProgramDirectory</c> — как у установки
/// Windows. Внутри APK файлы — не файлы, а записи архива, и по пути их не
/// открыть; поэтому поставка раскладывается в <c>FilesDir</c> в той же
/// раскладке, что build.cmd кладёт в установку (portability.md S1).
/// </para>
/// <para>
/// Только при первом запуске и при смене APK (номер версии и время установки):
/// ~600 файлов и 15 МБ на каждом запуске стоили бы секунды.
/// </para>
/// <para>
/// Личное человека не трогается никогда: настроек, подписок, своих правил
/// и своих списков в поставке нет, а дописанное в наши списки переносится
/// тем же <see cref="ListCarry"/>, что и обновление Windows-версии.
/// </para>
/// </remarks>
internal static class Payload
{
    private const string Root = "payload";

    /// <summary>Что распаковано и из какого APK — сверяется на каждом запуске.</summary>
    private static string StampPath(string files) => Path.Combine(files, "runtime", "payload.stamp");

    /// <summary>Какие файлы положила прошлая распаковка — чтобы убрать то, чего в новой нет.</summary>
    private static string ManifestPath(string files) => Path.Combine(files, "runtime", "payload.files");

    /// <summary>
    /// Личные файлы (CLAUDE.md, «Секреты»): их в поставке нет, а если бы
    /// оказались — распаковка их не перепишет и уборка не удалит.
    /// </summary>
    private static readonly string[] Personal =
    [
        "config/netzapret.json",
        "config/subscriptions.json",
        "config/rules.user.yaml",
        "config/preset-order.json",
        "config/dns.user.json",
        "config/subscription.cache.json",
        "config/hosts",
        "config/mobile.json",
    ];

    public static bool IsPersonal(string relative)
    {
        var path = relative.Replace('\\', '/');

        return Personal.Contains(path, StringComparer.OrdinalIgnoreCase)
            || path.StartsWith("config/lists/own/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains(".bak", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Распаковать, если APK сменился; вернуть, было ли что распаковывать.</summary>
    public static bool Install(Context context, string files)
    {
        Directory.CreateDirectory(Path.Combine(files, "runtime"));

        var stamp = Stamp(context);
        var stampPath = StampPath(files);

        if (File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp && Directory.Exists(Path.Combine(files, "presets")))
        {
            EnsureLayout(files);
            return false;
        }

        var assets = context.Assets ?? throw new InvalidOperationException("нет AssetManager — APK без поставки");

        if ((assets.List(Root) ?? []).Length == 0)
            throw new InvalidOperationException("в APK нет поставки (assets/payload) — собран без config/ и presets/");

        // Дописанное человеком в наши списки — до того, как их перепишет новая версия.
        // Нетронутые копии прошлой версии лежат движку как engines/zapret/lists/nz-<имя>.
        var userLists = Path.Combine(files, "config", "lists");
        var pristine = Path.Combine(files, "engines", "zapret", "lists");
        var additions = ListCarry.Additions(userLists, pristine);

        var shipped = new List<string>();
        Extract(assets, Root, files, shipped);

        ListCarry.Apply(additions, userLists);
        RemoveStale(files, shipped);
        EnsureLayout(files);

        File.WriteAllLines(ManifestPath(files), shipped);
        File.WriteAllText(stampPath, stamp);

        return true;
    }

    /// <summary>
    /// Папки и пустышки, которых библиотеки ждут от установки Windows.
    /// </summary>
    /// <remarks>
    /// Проверки «есть ли движок» смотрят <c>File.Exists(exe)</c>
    /// (SingBoxService, WinwsService, TunnelStatus.FindSingBox, Doctor —
    /// portability.md S1). Пустой файл проходит их, а запуск перехватывает
    /// хозяин (<c>EngineLauncher.Start</c>): на телефоне exe не запускается.
    /// Заодно FindSingBox не идёт искать sing-box.exe вверх по файловой системе.
    /// </remarks>
    public static void EnsureLayout(string files)
    {
        foreach (var directory in new[]
        {
            "runtime",
            Path.Combine("runtime", "libbox"),
            Path.Combine("runtime", "byedpi"),
            Path.Combine("config", "lists", "own"),
            Path.Combine("engines", "zapret", "user"),
        })
        {
            Directory.CreateDirectory(Path.Combine(files, directory));
        }

        foreach (var marker in new[]
        {
            Path.Combine("engines", "sing-box", "sing-box.exe"),
            Path.Combine("engines", "zapret", "exe", "winws2.exe"),
            Path.Combine("engines", "tg-ws-proxy", "tg-ws-proxy.exe"),
        })
        {
            var path = Path.Combine(files, marker);

            if (File.Exists(path))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }

        // Пины на телефоне — DNS-сервер hosts движка с этим файлом (config.md §3):
        // /etc/hosts без root не переписать. Пустой файл — пинов нет, но сервер
        // с несуществующим путём sing-box мог бы и не поднять.
        var hosts = Path.Combine(files, "config", "hosts");

        if (!File.Exists(hosts))
            File.WriteAllText(hosts, "");
    }

    /// <summary>Номер версии и время установки APK: меняется и у пересобранной отладочной сборки.</summary>
    private static string Stamp(Context context)
    {
        var info = context.PackageManager!.GetPackageInfo(context.PackageName!, (global::Android.Content.PM.PackageInfoFlags)0)!;
        long code = OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
        return $"{info.VersionName}|{code}|{info.LastUpdateTime}";
    }

    private static void Extract(AssetManager assets, string assetPath, string files, List<string> shipped)
    {
        var children = assets.List(assetPath) ?? [];

        // У файла детей нет; у папки — есть (пустых папок в APK не бывает).
        if (children.Length == 0)
        {
            var relative = assetPath[(Root.Length + 1)..];

            if (IsPersonal(relative))
                return;

            var target = Path.Combine(files, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Через временный файл: прерванная распаковка не оставит половину
            // списка, которую движок прочёл бы как весь список.
            var temporary = target + ".nz-new";

            using (var input = assets.Open(assetPath))
            using (var output = File.Create(temporary))
            {
                input.CopyTo(output);
            }

            File.Move(temporary, target, overwrite: true);
            shipped.Add(relative);
            return;
        }

        foreach (var child in children)
            Extract(assets, assetPath + "/" + child, files, shipped);
    }

    /// <summary>
    /// Убирает файлы прошлой поставки, которых в новой нет: иначе удалённый
    /// пресет остался бы в списке, а убранный список — в секциях.
    /// </summary>
    private static void RemoveStale(string files, IReadOnlyCollection<string> shipped)
    {
        var manifest = ManifestPath(files);

        if (!File.Exists(manifest))
            return;

        var now = shipped.ToHashSet(StringComparer.Ordinal);

        foreach (var relative in File.ReadAllLines(manifest))
        {
            if (relative.Length == 0 || now.Contains(relative) || IsPersonal(relative))
                continue;

            // Свои списки человека (config/lists/*.txt) — его, раз он их правил:
            // ListCarry уже перенёс дописанное, но файл, которого больше нет
            // в поставке, остаётся как есть — удалять правленое незачем.
            if (relative.StartsWith("config/lists/", StringComparison.Ordinal))
                continue;

            var path = Path.Combine(files, relative.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Занятый файл уберётся следующей распаковкой.
            }
        }
    }
}
