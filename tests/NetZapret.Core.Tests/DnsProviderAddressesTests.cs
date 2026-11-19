using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Автоузнавание адресов DoH-провайдеров по имени сервера (DnsProviderAddresses).
/// </summary>
public sealed class DnsProviderAddressesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nz-dns-addr-{Guid.NewGuid():N}");

    private string CachePath => Path.Combine(_dir, "dns-provider-addresses.json");

    public void Dispose()
    {
        DnsProviderAddresses.ClearCache();

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DnsProvider ComssLike(string address = "83.220.169.155") =>
        new("Comss.one", [address], address, "dns.comss.one", "/dns-query",
            Group: DnsGroup.Intermediary, AddressesByName: true, FormerAddresses: ["212.109.195.93"]);

    [Fact]
    public async Task Working_address_from_name_is_learned_and_old_becomes_former()
    {
        var now = new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
        var provider = ComssLike("83.220.169.155");

        var (updated, changed) = await DnsProviderAddresses.RefreshAsync(
            provider,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["141.133.184.24", "141.133.183.77"]),
            (ip, host, _, _) => Task.FromResult(ip == "141.133.184.24" && host == "dns.comss.one"),
            now,
            CancellationToken.None,
            CachePath);

        Assert.True(changed);
        Assert.Equal("141.133.184.24", updated.TlsAddress);
        Assert.True(DnsProviderAddresses.Matches(updated, "141.133.184.24"));
        Assert.True(DnsProviderAddresses.Matches(updated, "83.220.169.155"));
        Assert.True(DnsProviderAddresses.Matches(updated, "212.109.195.93"));

        // После сброса кэша памяти адрес читается с диска.
        DnsProviderAddresses.ClearCache();
        var applied = DnsProviderAddresses.Apply(provider, CachePath);
        Assert.Equal("141.133.184.24", applied.TlsAddress);
        Assert.False(DnsProviderAddresses.IsDue(provider, now + TimeSpan.FromHours(12), CachePath));
        Assert.True(DnsProviderAddresses.IsDue(provider, now + TimeSpan.FromHours(25), CachePath));
    }

    [Fact]
    public async Task Failing_first_candidate_is_skipped_and_private_or_stub_ips_are_ignored()
    {
        var probed = new List<string>();
        var provider = ComssLike("83.220.169.155");

        var (updated, changed) = await DnsProviderAddresses.RefreshAsync(
            provider,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(
                ["0.0.0.0", "127.0.0.1", "192.168.1.1", "10.0.0.5", "195.133.25.16", "141.133.183.77"]),
            (ip, _, _, _) =>
            {
                probed.Add(ip);
                return Task.FromResult(ip == "141.133.183.77");
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            CachePath);

        Assert.True(changed);
        Assert.Equal("141.133.183.77", updated.TlsAddress);
        Assert.Equal(["195.133.25.16", "141.133.183.77"], probed);
    }

    [Fact]
    public async Task Unanswered_refresh_keeps_previously_learned_address()
    {
        var now = new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
        var provider = ComssLike("83.220.169.155");

        await DnsProviderAddresses.RefreshAsync(
            provider,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["141.133.184.24"]),
            (_, _, _, _) => Task.FromResult(true),
            now,
            CancellationToken.None,
            CachePath);

        var applied = DnsProviderAddresses.Apply(provider, CachePath);
        Assert.Equal("141.133.184.24", applied.TlsAddress);

        var (afterFail, changed) = await DnsProviderAddresses.RefreshAsync(
            applied,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["155.212.172.101"]),
            (_, _, _, _) => Task.FromResult(false),
            now + TimeSpan.FromDays(2),
            CancellationToken.None,
            CachePath);

        Assert.False(changed);
        Assert.Equal("141.133.184.24", afterFail.TlsAddress);
        Assert.Equal("141.133.184.24", DnsProviderAddresses.Apply(provider, CachePath).TlsAddress);
    }

    [Fact]
    public async Task Static_anycast_provider_is_never_refreshed_by_name()
    {
        var google = DnsSurvey.Providers.First(p => p.Name == "Google");
        bool called = false;

        var (result, changed) = await DnsProviderAddresses.RefreshAsync(
            google,
            (_, _) =>
            {
                called = true;
                return Task.FromResult<IReadOnlyList<string>>(["8.8.8.8"]);
            },
            (_, _, _, _) => Task.FromResult(true),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            CachePath);

        Assert.False(called);
        Assert.False(changed);
        Assert.Equal(google.TlsAddress, result.TlsAddress);
        Assert.False(DnsProviderAddresses.IsDue(google, DateTimeOffset.UtcNow, CachePath));
    }

    [Fact]
    public async Task Current_address_still_present_in_round_robin_answer_does_not_churn()
    {
        var provider = ComssLike("141.133.184.24");
        var probed = new List<string>();

        var (result, changed) = await DnsProviderAddresses.RefreshAsync(
            provider,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["155.212.172.101", "141.133.184.24", "141.133.183.77"]),
            (ip, _, _, _) =>
            {
                probed.Add(ip);
                return Task.FromResult(true);
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            CachePath);

        Assert.False(changed);
        Assert.Equal("141.133.184.24", result.TlsAddress);
        Assert.Equal(["141.133.184.24"], probed);
    }

    [Fact]
    public void Former_addresses_resolve_to_current_provider_and_compile_to_current_ip()
    {
        var comss = DnsSurvey.ByAddress("83.220.169.155");
        Assert.NotNull(comss);
        Assert.Equal("Comss.one", comss!.Name);
        Assert.NotEqual("83.220.169.155", comss.TlsAddress);

        var zapret = DnsSurvey.ByAddress("38.180.241.191");
        Assert.NotNull(zapret);
        Assert.Equal("Zapret DNS", zapret!.Name);
        Assert.NotEqual("38.180.241.191", zapret.TlsAddress);

        var settings = new AppSettings { DnsServer = "83.220.169.155" };
        var compiled = DnsEngine.Compile(settings, ["8.8.8.8/32"], new Dictionary<string, string>(), EngineKeys.Generate());

        Assert.Contains($"\"server\": \"{comss.TlsAddress}\"", compiled.Json);
        Assert.Contains("\"server_name\": \"dns.comss.one\"", compiled.Json);
        Assert.DoesNotContain("83.220.169.155", compiled.Json);
    }
}
