using System.Security.Cryptography;
using System.Text;
using Avalonia;
using NetZapret.Core;

namespace NetZapret.Mobile.Preview;

/// <summary>
/// Окно просмотра интерфейса телефона на Windows.
/// </summary>
/// <remarks>
/// <para>
/// Папка данных — копия <c>config/</c> и <c>presets/</c> этого репозитория во
/// временной папке (<c>--files</c>), никогда не настоящая установка: просмотр
/// пишет в неё настройки, подписку-пример и запас подписки, и всё это должно
/// жить отдельно от программы владельца.
/// </para>
/// <para>
/// <c>--shots &lt;папка&gt;</c> — снимки разделов в PNG без окна (Shots).
/// <c>--fresh</c> — скопировать config/ и presets/ заново.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Выдуманные серверы подписки-примера: адреса из TEST-NET, ключи нулевые.</summary>
    public static IReadOnlyList<(string Name, string Protocol)> DemoServers { get; } =
    [
        ("🇳🇱 Нидерланды", "tls"),
        ("🇩🇪 Германия", "reality"),
        ("🇫🇮 Финляндия — XHTTP", "tls"),
        ("🇪🇪 Эстония", "reality"),
        ("🇹🇷 Турция", "tls"),
        ("🇺🇸 США — Нью-Йорк", "tls"),
        ("🇯🇵 Япония", "reality"),
        ("🇷🇺 Россия — обход белых списков", "tls"),
    ];

    public const string DemoUrl = "https://sub.example.invalid/preview-demo";

    [STAThread]
    public static int Main(string[] args)
    {
        var files = Argument(args, "--files")
            ?? Path.Combine(Path.GetTempPath(), "netzapret-mobile-preview");

        Prepare(files, args.Contains("--fresh"));

        // Как на телефоне: рабочая папка и папка программы — папка данных,
        // библиотеки читают config/… относительными путями.
        Environment.CurrentDirectory = files;
        AppPaths.ProgramDirectory = files;

        var host = new FakeHost(files);
        App.Host = host;

        if (Argument(args, "--shots") is { } shots)
            return Shots.Run(Path.GetFullPath(shots, AppContext.BaseDirectory), host, files);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string? Argument(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>Копия config/ и presets/ репозитория — один раз или заново по --fresh.</summary>
    private static void Prepare(string files, bool fresh)
    {
        var root = RepositoryRoot()
            ?? throw new InvalidOperationException("Не найден корень репозитория (NetZapret.sln) над " + AppContext.BaseDirectory);

        if (fresh && Directory.Exists(files))
            Directory.Delete(files, recursive: true);

        Directory.CreateDirectory(files);

        foreach (var folder in new[] { "config", "presets" })
        {
            var target = Path.Combine(files, folder);

            if (!Directory.Exists(target))
                Copy(Path.Combine(root, folder), target);
        }

        SeedDemo(files);
    }

    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;
        }

        return null;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    /// <summary>
    /// Подписка-пример с запасом: вкладке VPN есть что показать, а сети не нужно.
    /// </summary>
    /// <remarks>
    /// Запас кладётся тем же путём, что пишет SubscriptionPool (отпечаток
    /// ссылки), — выдуманные ключи с адресами TEST-NET (RFC 5737), их нигде нет.
    /// </remarks>
    public static void SeedDemo(string files)
    {
        var settings = Path.Combine(files, AppSettings.DefaultPath);

        if (!File.Exists(settings))
            (AppSettings.Fresh with { SubscriptionUrl = DemoUrl }).Save(settings);

        var book = Path.Combine(files, "config", "subscriptions.json");

        if (!File.Exists(book))
        {
            File.WriteAllText(book, $$"""
                {
                  "Entries": [
                    { "Name": "Пример", "Url": "{{DemoUrl}}", "Open": true, "InPool": true }
                  ],
                  "Keys": [],
                  "KeysInPool": true
                }
                """, new UTF8Encoding(false));
        }

        var reserveFolder = Path.Combine(files, "runtime", "subscriptions");
        Directory.CreateDirectory(reserveFolder);

        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DemoUrl)))[..24] + ".txt";
        var body = string.Join('\n', DemoServers.Select((s, i) =>
            $"vless://00000000-0000-4000-8000-{i:D12}@203.0.113.{i + 10}:443?security={(s.Protocol == "reality" ? "reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=00" : "tls")}&sni=demo.invalid&type=tcp#"
            + Uri.EscapeDataString(s.Name)));

        File.WriteAllText(Path.Combine(reserveFolder, name), body, new UTF8Encoding(false));
    }
}
