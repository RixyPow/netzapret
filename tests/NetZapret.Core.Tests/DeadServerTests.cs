using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Мёртвые серверы подписки и автоподбор.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по замеру 20.09: из девяти серверов подписки владельца отвечали
/// двое. Остальные семь движок опрашивал наравне с живыми и мог осесть
/// на любом из них — а осев, молчал до следующего замера. Туннель при этом
/// считался поднятым.
/// </para>
/// <para>
/// Из селектора мёртвые не убираются намеренно: закрепить такой сервер
/// руками — законное желание, он мог подняться между нашими замерами,
/// и лишать человека возможности это проверить нельзя.
/// </para>
/// </remarks>
public sealed class DeadServerTests
{
    private static ProxyServer Server(string tag) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = tag,
        Host = $"{tag.ToLowerInvariant()}.example.com",
        Port = 443,
        Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33",
        Transport = "tcp",
        Security = "tls",
    };

    /// <summary>Правила простейшие: проверяется группа выходов, а не они.</summary>
    private const string Rules = """
        mode: selective
        rules:
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
        default:
          mode: direct
        """;

    private static JsonElement Compile(IReadOnlyList<ProxyServer> servers, params string[] dead)
    {
        var result = new SingBoxConfigCompiler().Compile(
            RuleSetLoader.Load(Rules).RuleSet,
            servers,
            new SingBoxOptions { DeadServerTags = dead.ToHashSet(StringComparer.Ordinal) });

        return JsonDocument.Parse(result.Json).RootElement;
    }

    private static IReadOnlyList<string> MembersOf(JsonElement root, string type)
    {
        foreach (var outbound in root.GetProperty("outbounds").EnumerateArray())
        {
            if (outbound.TryGetProperty("type", out var t) && t.GetString() == type)
            {
                return outbound.GetProperty("outbounds")
                    .EnumerateArray()
                    .Select(x => x.GetString()!)
                    .ToList();
            }
        }

        return [];
    }

    /// <summary>Мёртвый не участвует в автоподборе.</summary>
    [Fact]
    public void A_dead_server_is_kept_out_of_the_latency_group()
    {
        var root = Compile([Server("Финляндия"), Server("Германия")], "Германия");

        var auto = MembersOf(root, "urltest");

        Assert.Contains("Финляндия", auto);
        Assert.DoesNotContain("Германия", auto);
    }

    /// <summary>
    /// Но остаётся в селекторе — закрепить его руками можно.
    /// </summary>
    /// <remarks>
    /// Сервер мог подняться между нашими замерами. Убрав его отовсюду,
    /// мы лишили бы человека единственного способа это выяснить.
    /// </remarks>
    [Fact]
    public void A_dead_server_can_still_be_pinned_by_hand()
    {
        var root = Compile([Server("Финляндия"), Server("Германия")], "Германия");

        Assert.Contains("Германия", MembersOf(root, "selector"));
    }

    /// <summary>
    /// Вся подписка мертва — выводить некого.
    /// </summary>
    /// <remarks>
    /// Группа без единого участника не даст конфигу запуститься вовсе,
    /// и туннель не поднимется совсем. Перебирать трупы всё же лучше:
    /// какой-то из них может ожить.
    /// </remarks>
    [Fact]
    public void An_entirely_dead_subscription_is_not_emptied()
    {
        var root = Compile([Server("Финляндия"), Server("Германия")], "Финляндия", "Германия");

        var auto = MembersOf(root, "urltest");

        Assert.Equal(2, auto.Count);
    }

    /// <summary>Без замеров ничего не выводится.</summary>
    /// <remarks>
    /// Первый запуск: замеров ещё не было, и считать всех живыми —
    /// единственное честное поведение.
    /// </remarks>
    [Fact]
    public void Without_measurements_nobody_is_excluded()
    {
        var root = Compile([Server("Финляндия"), Server("Германия")]);

        Assert.Equal(2, MembersOf(root, "urltest").Count);
    }

    /// <summary>
    /// Счёт неудач ведёт сам кэш, а не тот, кто пишет замер.
    /// </summary>
    /// <remarks>
    /// Мест записи три — меню консоли, команда probe и раздел «VPN».
    /// Считай каждое порознь — получились бы три расходящихся счётчика.
    /// </remarks>
    [Fact]
    public void The_cache_counts_the_failures_itself()
    {
        var cache = ServerHealthCache.Load(Path.Combine(Path.GetTempPath(), $"нет-{Guid.NewGuid():N}.json"));

        foreach (var _ in Enumerable.Range(0, 3))
            cache.Set(Health("Германия", success: false));

        Assert.Equal(3, cache.Find("Германия")!.Failures);
        Assert.Contains("Германия", cache.Dead(3));
    }

    /// <summary>Удавшаяся проверка обнуляет счёт.</summary>
    /// <remarks>
    /// Иначе сервер, отлежавшийся и вернувшийся, остался бы вычеркнутым
    /// навсегда — а именно так ведут себя подписки: сервер падает,
    /// владелец его чинит.
    /// </remarks>
    [Fact]
    public void A_successful_check_forgives_the_past()
    {
        var cache = ServerHealthCache.Load(Path.Combine(Path.GetTempPath(), $"нет-{Guid.NewGuid():N}.json"));

        cache.Set(Health("Германия", success: false));
        cache.Set(Health("Германия", success: false));
        cache.Set(Health("Германия", success: true));

        Assert.Equal(0, cache.Find("Германия")!.Failures);
        Assert.Empty(cache.Dead(3));
    }

    /// <summary>
    /// Разовый отказ мёртвым не считается.
    /// </summary>
    /// <remarks>
    /// Сеть моргнула, узел перегружен — это случается и у исправного
    /// сервера. Вычёркивать по одной неудаче значило бы разбрасываться
    /// теми немногими, что ещё живы.
    /// </remarks>
    [Fact]
    public void One_failure_is_not_death()
    {
        var cache = ServerHealthCache.Load(Path.Combine(Path.GetTempPath(), $"нет-{Guid.NewGuid():N}.json"));

        cache.Set(Health("Германия", success: false));

        Assert.Empty(cache.Dead(3));
    }

    private static ServerHealth Health(string tag, bool success) => new()
    {
        Tag = tag,
        Success = success,
        CheckedAt = DateTimeOffset.Now,
    };
}
