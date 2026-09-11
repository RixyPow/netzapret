using System.Diagnostics;
using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Бесплатный WARP: один выход, MASQUE.
/// </summary>
/// <remarks>
/// Второй выход, по WireGuard, отсюда убран вместе со всей обвязкой —
/// заведением ключей, регистрацией в Cloudflare и хранением записи. Работать
/// он не мог: Cloudflare кладёт идентификатор записи в поле <c>reserved</c>
/// каждого пакета, а sing-box 1.14 убрал это поле из настройки пира вовсе.
/// Проверено на живой сети: рукопожатие проходит, данные не возвращаются
/// ни на одном из пяти портов.
/// </remarks>
public class WarpTests
{
    /// <summary>
    /// MASQUE описывается двумя строками: узла у него нет, движок выбирает
    /// его сам. Поля server и server_port он объявляет незнакомыми
    /// и отказывается читать конфиг целиком — то есть лишнее поле здесь
    /// роняет туннель, а не портит один выход.
    /// </summary>
    [Fact]
    public void MasqueOutboundCarriesNoServerAddress()
    {
        var outbound = Compile(Warp.MasqueServer())
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag);

        Assert.Equal("masque", outbound.GetProperty("type").GetString());
        Assert.False(outbound.TryGetProperty("server", out _));
        Assert.False(outbound.TryGetProperty("server_port", out _));
    }

    /// <summary>
    /// Даже когда адрес выяснен заранее, в MASQUE его подставлять нельзя:
    /// поля для него нет, и конфиг пробника перестал бы читаться.
    /// </summary>
    [Fact]
    public void ResolvedAddressIsNotPinnedIntoMasque()
    {
        var json = new SingBoxConfigCompiler()
            .CompileProbeConfig(Warp.MasqueServer(), 21080, null, "warn", "104.16.24.84");

        var outbound = JsonDocument.Parse(json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == "probe-out");

        Assert.False(outbound.TryGetProperty("server", out _));
    }

    /// <summary>
    /// Кэш движка включается только вместе с MASQUE: в нём лежит учётная
    /// запись, которую движок заводит себе сам, и без кэша он регистрировался
    /// бы заново при каждом запуске.
    /// </summary>
    [Fact]
    public void EngineCacheAppearsOnlyWithMasque()
    {
        var withMasque = Compile(Warp.MasqueServer()).GetProperty("experimental");

        Assert.True(withMasque.TryGetProperty("cache_file", out var cache));
        Assert.True(cache.GetProperty("enabled").GetBoolean());
        Assert.True(cache.GetProperty("store_masque_config").GetBoolean());

        // Полным путём, и это не придирка: относительный решается от текущего
        // каталога движка, где runtime может не существовать вовсе. Движок
        // его не создаёт, а падает на старте — вместе со всем туннелем.
        var path = cache.GetProperty("path").GetString()!;

        Assert.True(Path.IsPathFullyQualified(path), $"путь к кэшу не полный: {path}");
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)), "каталог кэша не заведён");

        var withoutMasque = Compile(Ordinary()).GetProperty("experimental");

        Assert.False(withoutMasque.TryGetProperty("cache_file", out _));
    }

    /// <summary>
    /// WARP встаёт в группы наравне с серверами подписки: пока те живы,
    /// автоподбор берёт их — они быстрее, — а когда лягут, останется он.
    /// </summary>
    [Fact]
    public void WarpJoinsTheSelectorBesideSubscriptionServers()
    {
        var root = Compile(Ordinary(), Warp.MasqueServer());

        foreach (var group in new[] { "auto-latency", "auto" })
        {
            var members = root.GetProperty("outbounds").EnumerateArray()
                .First(o => o.GetProperty("tag").GetString() == group)
                .GetProperty("outbounds").EnumerateArray()
                .Select(m => m.GetString())
                .ToList();

            Assert.Contains(Warp.MasqueTag, members);
            Assert.Contains("NL", members);
        }
    }

    /// <summary>
    /// MASQUE не замеряется отдельным пробником, и это его свойство, а не
    /// сбой: запись движок держит в кэше работающего экземпляра, а файл занят
    /// им же. Меряет его сам движок через Clash API.
    /// </summary>
    [Fact]
    public void MasqueIsNotMeasurableByTheProbe()
    {
        Assert.False(Warp.MasqueServer().IsMeasurable);
        Assert.True(Warp.MasqueServer().IsSelfRegistering);
        Assert.True(Ordinary().IsMeasurable);
    }

    /// <summary>Выход ровно один, и ключей он не требует.</summary>
    [Fact]
    public void OnlyMasqueRemains()
    {
        Assert.Equal([Warp.MasqueTag], Warp.Exits().Select(s => s.Tag));
        Assert.Empty(Warp.MasqueServer().Credential);
    }

    /// <summary>
    /// Сквозная проверка настоящим движком. Пропускается, если его нет:
    /// в репозитории он не хранится.
    /// </summary>
    [Fact]
    public void ConfigWithWarpPassesSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """);

        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [Ordinary(), Warp.MasqueServer()],
            new SingBoxOptions());

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-warp-check-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, result.Json);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"sing-box check не пройден: {stderr}");
        }
        finally
        {
            File.Delete(path);
            File.Delete(SingBoxConfigCompiler.StampPathFor(path));
        }
    }

    private static ProxyServer Ordinary() => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Security = "tls",
    };

    private static JsonElement Compile(params ProxyServer[] servers)
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """);

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private static string? FindSingBox()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools))
            {
                var found = Directory
                    .EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found is not null)
                    return found;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
