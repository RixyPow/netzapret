using System.IO.Compression;
using System.Text;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Локальные файлы подписок (mihomo YAML, Xray JSON, base64): разбор, хранение, пул и отчёт.</summary>
public sealed class LocalSubscriptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"netzapret-local-sub-{Guid.NewGuid():N}");

    private const string MihomoYaml = """
        # Конфиг mihomo / Clash Meta с rule-providers (URL внутри не должны мешать распознаванию)
        mixed-port: 7890
        rule-providers:
          direct:
            type: http
            url: "https://example.com/rules.yaml"
        proxies:
          - name: "🇩🇪 Германия"
            type: vless
            server: de.example.com
            port: 443
            uuid: 11111111-2222-3333-4444-555555555555
            tls: true
            flow: xtls-rprx-vision
            servername: www.microsoft.com
            reality-opts:
              public-key: jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0
              short-id: abcd
          - name: "🇳🇱 Нидерланды"
            type: trojan
            server: nl.example.com
            port: 443
            password: super-secret-trojan-pass
            sni: nl.example.com
        """;

    public LocalSubscriptionsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void KeyImport_recognizes_mihomo_yaml_with_bom_and_urls_inside()
    {
        var found = KeyImport.FromText("\uFEFF" + MihomoYaml, "v12.55_Mihomo_Clash_Meta");

        Assert.NotNull(found.SubscriptionBody);
        Assert.Null(found.SubscriptionUrl);
        Assert.Empty(found.Keys);
        Assert.Null(found.Problem);
        Assert.Equal("mihomo / Clash Meta", found.SubscriptionFormat);
        Assert.Equal(2, found.ServerCount);
        Assert.Equal(2, found.UsableCount);
    }

    [Fact]
    public void KeyImport_recognizes_xray_json_and_base64_subscriptions()
    {
        const string xray = """
            {
              "outbounds": [
                {
                  "tag": "proxy-de",
                  "protocol": "vless",
                  "settings": {
                    "vnext": [{ "address": "x.example.com", "port": 443, "users": [{ "id": "11111111-2222-3333-4444-555555555555" }] }]
                  },
                  "streamSettings": { "network": "tcp", "security": "tls" }
                }
              ]
            }
            """;

        var jsonFound = KeyImport.FromText(xray);
        Assert.NotNull(jsonFound.SubscriptionBody);
        Assert.Equal("Xray JSON", jsonFound.SubscriptionFormat);
        Assert.Equal(1, jsonFound.ServerCount);

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "trojan://pass@a.example.com:443#A\ntrojan://pass@b.example.com:443#B\n"));
        var b64Found = KeyImport.FromText(b64);
        Assert.NotNull(b64Found.SubscriptionBody);
        Assert.Equal(2, b64Found.ServerCount);
        Assert.Empty(b64Found.Keys);
    }

    [Fact]
    public void KeyImport_reports_empty_mihomo_yaml_instead_of_adding_it()
    {
        var found = KeyImport.FromText("proxies:\n");

        Assert.Null(found.SubscriptionBody);
        Assert.NotNull(found.Problem);
        Assert.Contains("mihomo", found.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("local://../secret")]
    [InlineData("local://short")]
    [InlineData("local://not-hex-chars!")]
    [InlineData("https://example.com/sub")]
    [InlineData("")]
    public void LocalSubscriptions_rejects_invalid_or_traversal_urls(string url)
    {
        Assert.False(LocalSubscriptions.IsLocal(url));
        Assert.Null(LocalSubscriptions.FilePath(url, _root));
        Assert.Null(LocalSubscriptions.Read(url, _root));
        Assert.False(LocalSubscriptions.Replace(url, MihomoYaml, _root));
    }

    [Fact]
    public async Task Save_read_replace_pool_and_cleanup_work_end_to_end()
    {
        var localDir = Path.Combine(_root, "local");
        var reserveDir = Path.Combine(_root, "reserve");

        var url = LocalSubscriptions.Save(MihomoYaml, localDir);
        Assert.True(LocalSubscriptions.IsLocal(url));
        Assert.Empty(SubscriptionHosts.From([url]));

        // Чтение напрямую и через пул (deferPanels: true не откладывает локальный файл).
        var read = await SubscriptionPool.ReadOneAsync(url, force: true, CancellationToken.None, reserveDir, localDir);
        Assert.Equal(SubscriptionReadSource.Local, read.Source);
        Assert.NotNull(read.Info);
        Assert.Equal(2, read.Info!.Servers.Count);

        Assert.Null(SubscriptionPool.KeptWhileAsking(url, force: true, DateTimeOffset.Now, reserveDir));

        var pool = await SubscriptionPool.BuildAsync(
            [new PoolSource("Mihomo", url)],
            CancellationToken.None,
            reserveDir,
            deferPanels: true,
            localDirectory: localDir);

        Assert.Equal(2, pool.Servers.Count);
        Assert.Single(pool.Parts);
        Assert.False(pool.Parts[0].Deferred);
        Assert.False(pool.Parts[0].FromReserve);

        // Замена содержимого на месте.
        var updatedYaml = """
            proxies:
              - name: "🇫🇮 Финляндия"
                type: trojan
                server: fi.example.com
                port: 443
                password: updated-password
            """;
        Assert.True(LocalSubscriptions.Replace(url, updatedYaml, localDir));
        var after = LocalSubscriptions.Read(url, localDir);
        Assert.NotNull(after);
        Assert.Single(after!.Value.Info.Servers);
        Assert.Equal("🇫🇮 Финляндия", after.Value.Info.Servers[0].Tag);

        // Удаление из книги чистит файл; повторное чтение честно сообщает об отсутствии файла.
        Assert.Equal(1, LocalSubscriptions.KeepOnly([], localDir));
        var missing = await SubscriptionPool.ReadOneAsync(url, force: false, CancellationToken.None, reserveDir, localDir);
        Assert.Equal(SubscriptionReadSource.None, missing.Source);
        Assert.Contains("не найден", missing.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoLiveSubscriptionRefresh_skips_local_subscriptions()
    {
        int calls = 0;
        var refresher = new NoLiveSubscriptionRefresh(
            Path.Combine(_root, "refresh-state.json"),
            read: (_, _) =>
            {
                calls++;
                return Task.FromResult(new SubscriptionRead(null, null, SubscriptionReadSource.None, "should not run"));
            });

        var result = await refresher.TryAsync(["local://0123456789abcdef"], CancellationToken.None);

        Assert.False(result.Attempted);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Digest_and_support_report_summarize_local_subscription_without_leaking_keys()
    {
        var localDir = Path.Combine(_root, "config", "subscriptions");
        var url = LocalSubscriptions.Save(MihomoYaml, localDir);

        File.WriteAllText(
            Path.Combine(_root, "config", "subscriptions.json"),
            $$"""{ "Entries": [ { "Name": "Mihomo", "Url": "{{url}}", "InPool": true } ] }""",
            new UTF8Encoding(false));

        var digest = SubscriptionDigest.Describe([url], Path.Combine(_root, "runtime", "subscriptions"), localDir);
        Assert.Contains("локальный файл от", digest);
        Assert.Contains("de.example.com:443", digest);
        Assert.DoesNotContain("11111111-2222-3333-4444-555555555555", digest);
        Assert.DoesNotContain("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", digest);
        Assert.DoesNotContain("super-secret-trojan-pass", digest);

        var report = SupportReport.Create("1.6.7", directory: Path.Combine(_root, "reports"), root: _root);
        using var zip = ZipFile.OpenRead(report.Path);

        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("subscriptions/", StringComparison.OrdinalIgnoreCase));
        var subsEntry = Assert.Single(zip.Entries, e => e.FullName == "subscriptions.txt");
        using var reader = new StreamReader(subsEntry.Open());
        var text = reader.ReadToEnd();

        Assert.Contains("локальный файл от", text);
        Assert.DoesNotContain("11111111-2222-3333-4444-555555555555", text);
        Assert.DoesNotContain("super-secret-trojan-pass", text);
    }
}
