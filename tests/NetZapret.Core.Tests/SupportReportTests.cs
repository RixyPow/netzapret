using System.IO.Compression;
using System.Text;
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
            $"{{ \"SubscriptionUrl\": \"{Subscription}\", \"DnsThroughTunnel\": true }}");

        File.WriteAllText(Path.Combine(_root, "config", "rules.user.yaml"),
            "rules:\n  - match: domain\n    value: \"*.example.org\"\n    mode: proxy\n");

        // Вторая подписка — только в книге: отчёт обязан найти её сам.
        File.WriteAllText(Path.Combine(_root, "config", "subscriptions.json"),
            $"{{ \"Entries\": [ {{ \"Name\": \"вторая\", \"Url\": \"{BookOnly}\" }} ] }}");
        File.AppendAllText(Path.Combine(_root, "runtime", "supervisor.log"), $"вторая: {BookOnly}\n");

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

        if (profile.Length > 0)
            Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);

        // А нужное для разбора — на месте.
        Assert.Contains("не прочиталась", text);
        Assert.Contains("DNS: 8.8.8.8, через туннель", text);
        Assert.Contains("*.example.org", text);
        Assert.Contains("ERROR", text);
    }

    [Fact]
    public void ALinkKeepsItsHostButNotItsPath()
    {
        var redacted = SupportReport.Redact(
            "fetch https://api.github.com/repos/x/y/releases?page=2 failed", []);

        Assert.Contains("https://api.github.com/…", redacted);
        Assert.DoesNotContain("releases", redacted);
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
