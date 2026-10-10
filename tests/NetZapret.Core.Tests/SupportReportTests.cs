using System.IO.Compression;
using System.Text;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Отчёт для разбора уходит в общий чат — секретов в нём быть не должно.
/// </summary>
public sealed class SupportReportTests : IDisposable
{
    // Выдуманные: настоящей ссылке в тестах не место (CLAUDE.md).
    private const string Subscription = "https://panel.example.net/sub/Zx9TOKENqQ7-abc?flag=1";
    private const string Vless = "vless://0b3e6d2c-1111-4a2b-9c3d-123456789abc@203.0.113.9:443?security=reality&pbk=KEY#Швеция";
    private const string BookOnly = "https://sub2.example.org/api/BOOKTOKEN";
    private const string Uuid ="0b3e6d2c-2222-4a2b-9c3d-123456789abc";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-report-{Guid.NewGuid():N}");

    public SupportReportTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "runtime"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void NoSecretLeavesInTheArchive()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        File.WriteAllText(Path.Combine(_root, "runtime", "supervisor.log"),
            $"[19:28:01] подписка: «vpn» не прочиталась, ссылка {Subscription}\n"
            + $"сервер {Vless}\n"
            + $"id {Uuid}\n"
            + $"путь {profile}\\Projects\\netzapret\\runtime\n");

        File.WriteAllText(Path.Combine(_root, "runtime", "sing-box.log"),
            "\u001b[31mERROR\u001b[0m dial: {\"password\": \"hunter2\", \"private_key\": \"AAAA\"}\n");

        File.WriteAllText(Path.Combine(_root, "config", "netzapret.json"),
            $"{{ \"SubscriptionUrl\": \"{Subscription}\", \"DnsVia\": \"Tunnel\" }}");

        File.WriteAllText(Path.Combine(_root, "config", "rules.user.yaml"),
            "rules:\n  - match: domain\n    value: \"*.example.org\"\n    mode: proxy\n");

        // Вторая подписка — только в книге: отчёт обязан найти её сам.
        File.WriteAllText(Path.Combine(_root, "config", "subscriptions.json"),
            $"{{ \"Entries\": [ {{ \"Name\": \"вторая\", \"Url\": \"{BookOnly}\" }} ] }}");
        File.AppendAllText(Path.Combine(_root, "runtime", "supervisor.log"), $"вторая: {BookOnly}\n");

        // Две проверки блокировок: в отчёт идёт свежая, и о ней сказано в сводке.
        Directory.CreateDirectory(Path.Combine(_root, "reports"));
        var old = Path.Combine(_root, "reports", "blockcheck-2026-09-20-1000.txt");
        File.WriteAllText(old, "старая проверка");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-5));
        File.WriteAllText(Path.Combine(_root, "reports", "blockcheck-2026-09-25-2327.txt"),
            $"Проверка блокировок\nyoutube.com ок\nссылка {Subscription}\n"
            + "Выход: 203.0.113.77, RU — мимо туннеля\n");

        var result = SupportReport.Create("0.8.3 (1)", root: _root);
        var text = ReadAll(result.Path);

        // Имена внутри архива — латиницей: русские у части распаковщиков
        // превращаются в кракозябры.
        using (var archive = ZipFile.OpenRead(result.Path))
            Assert.All(archive.Entries, e => Assert.True(e.FullName.All(char.IsAscii), $"не латиницей: {e.FullName}"));

        Assert.DoesNotContain("Zx9TOKEN", text);
        Assert.DoesNotContain("BOOKTOKEN", text);
        Assert.DoesNotContain("sub2.example.org", text);
        Assert.Contains("Подписок: 1", text);
        Assert.DoesNotContain("panel.example.net/sub", text);
        Assert.DoesNotContain("security=reality", text);
        Assert.DoesNotContain(Uuid, text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("AAAA", text);
        Assert.DoesNotContain("\u001b[", text);

        // Выход мимо туннеля — домашний адрес человека (отчёт владельца 06.10).
        Assert.DoesNotContain("203.0.113.77", text);
        Assert.Contains("Выход: <адрес скрыт>, RU — мимо туннеля", text);

        if (profile.Length > 0)
            Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);

        // А нужное для разбора — на месте.
        Assert.Contains("не прочиталась", text);
        Assert.Contains("DNS: 8.8.4.4, через туннель", text);
        Assert.Contains("*.example.org", text);
        Assert.Contains("ERROR", text);
        Assert.Contains("youtube.com ок", text);
        Assert.DoesNotContain("старая проверка", text);
        Assert.Contains("Проверка блокировок: blockcheck-2026-09-25-2327.txt", text);
    }

    /// <summary>
    /// Замеры скорости — в отчёте (владелец 30.09: «есть ли последние результаты
    /// проверки в отчёте?»): свежий замер каждого пути в сводке, все — в speed.txt.
    /// </summary>
    [Fact]
    public void SpeedMeasurementsAreInTheReport()
    {
        var history = Path.Combine(_root, "runtime", "speed-history.json");
        var noon = new DateTimeOffset(2026, 9, 30, 12, 53, 0, DateTimeOffset.Now.Offset);

        SpeedHistory.Add(new SpeedEntry
        {
            At = noon, Tunnel = true, Exit = "Эстония — TLS XHTTP",
            DownMbps = 156.2, UpMbps = 48.2, PingMs = 37, Country = "EE", Node = "ARN", Bytes = 217L * 1024 * 1024,
        }, history);

        SpeedHistory.Add(new SpeedEntry
        {
            At = noon.AddMinutes(1), Tunnel = false,
            DownMbps = 354.7, UpMbps = 341.6, PingMs = 8, Country = "RU", Node = "DME",
        }, history);

        var result = SupportReport.Create("0.10.0 (4)", root: _root);

        Assert.Contains("speed.txt", result.Files);

        using var archive = ZipFile.OpenRead(result.Path);

        var summary = Read(archive, "summary.txt");
        var speed = Read(archive, "speed.txt");

        Assert.Contains("Замер скорости напрямую, 30.09.2026 12:54: скачивание 355 Мбит/с", summary);
        Assert.Contains("Замер скорости через туннель, 30.09.2026 12:53: скачивание 156 Мбит/с", summary);

        Assert.Contains("через туннель", speed);
        Assert.Contains("сервер Эстония — TLS XHTTP; страна выхода EE, узел Cloudflare ARN; ушло 217 МБ", speed);

        // Свежий — первым.
        Assert.True(speed.IndexOf("12:54", StringComparison.Ordinal) < speed.IndexOf("12:53", StringComparison.Ordinal));

        static string Read(ZipArchive archive, string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Наблюдение — в отчёте сводкой по программам и журналом целиком
    /// (владелец 10.10); без журнала сводка говорит, что не включали.
    /// </summary>
    [Fact]
    public void TheWatchJournalAndItsSummaryAreInTheReport()
    {
        var without = SupportReport.Create("0.14.5 (3)", root: _root);

        Assert.Contains("watch-summary.txt", without.Files);
        Assert.DoesNotContain("watch.log", without.Files);
        Assert.Contains("не включали", ReadAll(without.Path));

        var entry = Core.Connections.WatchEntry.From(
            new Core.Connections.ConnectionEvent
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = Core.Connections.ProtocolKind.Tcp,
                RemoteAddress = System.Net.IPAddress.Parse("142.250.74.46"),
                RemotePort = 443,
                ExecutablePath = @"C:\Program Files\chrome.exe",
                Hostname = "youtube.com",
            },
            new Core.Rules.RuleDecision { Mode = Core.Rules.RoutingMode.Desync, Reason = "youtube" });

        File.WriteAllLines(Path.Combine(_root, "runtime", "watch.log"),
        [
            Core.Connections.WatchEntry.Session(DateTimeOffset.Now, Core.Connections.WatchEntry.Started + ": режим «Гибрид»"),
            entry.ToLine(),
        ]);

        var with = SupportReport.Create("0.14.5 (3)", root: _root, directory: Path.Combine(_root, "second"));

        Assert.Contains("watch.log", with.Files);

        var text = ReadAll(with.Path);

        Assert.Contains("youtube.com:443", text);
        Assert.Matches(@"chrome\.exe\s+1\s+0\s+1", text);
    }

    [Fact]
    public void WithoutMeasurementsTheReportSaysSo()
    {
        var result = SupportReport.Create("0.10.0 (4)", root: _root);

        Assert.DoesNotContain("speed.txt", result.Files);
        Assert.Contains("Замер скорости: не проводился", ReadAll(result.Path));
    }

    /// <summary>
    /// Сторож серверов и прошлое поколение журналов — в отчёте (владелец 06.10:
    /// «всё, что может помочь в диагностике»); прочитанное с машины — только по просьбе.
    /// </summary>
    [Fact]
    public void ServersAndThePreviousLogGenerationAreInTheReport()
    {
        var now = new DateTimeOffset(2026, 10, 6, 21, 31, 22, DateTimeOffset.Now.Offset);

        File.WriteAllText(Path.Combine(_root, "runtime", "server-health.json"), $$"""
            [
              { "Tag": "Бельгия", "Success": true, "LatencyMs": 183, "CheckedAt": "{{now:O}}",
                "Failures": 0, "Recent": [true, true, false, true] },
              { "Tag": "Эстония", "Success": false, "CheckedAt": "{{now.AddMinutes(-2):O}}",
                "Failures": 2, "Recent": [true, false, false, false, true, false] },
              { "Tag": "Россия", "Success": false, "CheckedAt": "{{now.AddMinutes(-3):O}}",
                "Failures": 5, "Recent": [false, false, false, false, false] }
            ]
            """);

        ExitHistory.Note("Бельгия", now, Path.Combine(_root, "runtime", "exit-history.json"));

        File.WriteAllText(Path.Combine(_root, "runtime", "sing-box.log"), "свежий запуск\n");
        File.WriteAllText(Path.Combine(_root, "runtime", "sing-box.log.1"), "первая ошибка прошлого запуска\n");

        var result = SupportReport.Create("0.13.1 (3)", root: _root);

        Assert.Contains("servers.txt", result.Files);
        Assert.Contains("sing-box.1.log", result.Files);

        // Читается с машины, а не из корня — без просьбы этого нет.
        Assert.DoesNotContain("doctor.txt", result.Files);
        Assert.DoesNotContain("voice.txt", result.Files);
        Assert.DoesNotContain("hosts.txt", result.Files);

        var text = ReadAll(result.Path);

        Assert.Contains("Бельгия: ответил за 183 мс, 06.10 21:31:22; ++−+", text);
        Assert.Contains("Эстония: не ответил", text);
        Assert.Contains("промахов подряд 2; мигающий", text);

        // Молчащий совсем — не «мигающий», хоть сторож и отбирает их одинаково.
        Assert.Contains("−−−−−; промахов подряд 5", text);
        Assert.DoesNotContain("промахов подряд 5; мигающий", text);
        Assert.Contains("06.10 21:31:22  Бельгия", text);
        Assert.Contains("первая ошибка прошлого запуска", text);
    }

    /// <summary>Из hosts — строки с адресами и наш блок, без пояснений Microsoft.</summary>
    [Fact]
    public void TheHostsExcerptKeepsPinsAndOurBlock()
    {
        var hosts = string.Join("\r\n",
            "# Copyright (c) 1993-2009 Microsoft Corp.",
            "#\t127.0.0.1       localhost",
            "",
            "149.154.167.220 web.telegram.org",
            HostsEditor.BlockBegin,
            $"57.144.218.34 instagram.com # {HostsEditor.DesyncMark}",
            HostsEditor.BlockEnd);

        var excerpt = SupportReport.HostsExcerpt(hosts);

        Assert.Contains("Строк с адресами: 2", excerpt);
        Assert.Contains("149.154.167.220 web.telegram.org", excerpt);
        Assert.Contains($"57.144.218.34 instagram.com # {HostsEditor.DesyncMark}", excerpt);
        Assert.Contains(HostsEditor.BlockBegin + "\n", excerpt);
        Assert.DoesNotContain("Copyright", excerpt);
        Assert.DoesNotContain("localhost", excerpt);
    }

    /// <summary>Проверки «Диагностики» текстом: итог первым, поломка видна словом.</summary>
    [Fact]
    public void DoctorTextStartsWithTheVerdict()
    {
        var text = Doctor.Describe(
        [
            new("Десинк", [new("winws2.exe на месте.", DoctorLevel.Ok)]),
            new("Браузеры и сертификаты", [new("Chrome резолвит имена сам.", DoctorLevel.Warn)]),
            new("Супервизор", [new("sing-box: процесс умер.", DoctorLevel.Bad)]),
        ]);

        Assert.StartsWith("Проверок 3: поломок 1, оговорок 1.", text);
        Assert.Contains("  [ок] winws2.exe на месте.", text);
        Assert.Contains("  [оговорка] Chrome резолвит имена сам.", text);
        Assert.Contains("  [ПОЛОМКА] sing-box: процесс умер.", text);
    }

    [Fact]
    public void ALinkKeepsItsHostButNotItsPath()
    {
        var redacted = SupportReport.Redact(
            "fetch https://api.github.com/repos/x/y/releases?page=2 failed", []);

        Assert.Contains("https://api.github.com/…", redacted);
        Assert.DoesNotContain("releases", redacted);
    }

    /// <summary>
    /// Имя пользователя в путях скрыто, как бы движок их ни писал.
    /// </summary>
    /// <remarks>
    /// winws2 под Cygwin пишет <c>/cygdrive/c/Users/имя/…</c>, родная сборка
    /// (с 07.10) — <c>C:/Users/имя/…</c>: прямыми слэшами, как мы их ей и даём
    /// (<c>WinwsCommandLine.Forward</c>). Второе до 07.10 не скрывалось.
    /// </remarks>
    [Fact]
    public void TheProfileIsHiddenInEverySpelling()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
            return;

        var forward = profile.Replace('\\', '/');
        var cygwin = "/cygdrive/" + char.ToLowerInvariant(profile[0]) + profile[2..].Replace('\\', '/');
        var log = string.Join('\n',
            $"Loading hostlist {profile}\\nz\\runtime\\desync-keep.txt",
            $"Loading hostlist {forward}/nz/runtime/desync-keep.txt",
            $"Loading hostlist {cygwin}/nz/runtime/desync-keep.txt");

        var redacted = SupportReport.Redact(log, []);
        var user = Path.GetFileName(profile);

        Assert.DoesNotContain(user, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, redacted.Split("%USERPROFILE%").Length - 1);
    }

    [Fact]
    public void ALongLogKeepsItsBeginningAndEnd()
    {
        // Первая ошибка — в начале: хвост из сотни одинаковых строк — это эхо.
        var lines = Enumerable.Range(1, 5000).Select(i => $"строка {i}");
        var excerpt = SupportReport.Excerpt(string.Join('\n', lines), 10, 20);

        Assert.Contains("строка 1\n", excerpt);
        Assert.Contains("строка 5000", excerpt);
        Assert.DoesNotContain("строка 2500", excerpt);
        Assert.Contains("пропущено строк: 4970", excerpt);
    }

    private static string ReadAll(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var all = new StringBuilder();

        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            all.AppendLine(reader.ReadToEnd());
        }

        return all.ToString();
    }
}
