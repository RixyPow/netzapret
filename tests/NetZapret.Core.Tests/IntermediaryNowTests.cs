using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Приговор «что посредник отдаёт сейчас» для пунктов окна пина
/// (<see cref="IntermediaryDns.Judge"/>).
/// </summary>
public sealed class IntermediaryNowTests
{
    private static IReadOnlyList<IPAddress> A(params string[] a) => a.Select(IPAddress.Parse).ToList();

    private static Intermediary Server(string key) => IntermediaryDns.ByKey(key)!;

    private static ServerAnswers Answers(IReadOnlyList<IPAddress>? relay = null, params (string Name, IReadOnlyList<IPAddress> Addresses)[] names) =>
        new(names.ToDictionary(n => n.Name, n => n.Addresses, StringComparer.OrdinalIgnoreCase), relay ?? []);

    [Fact]
    public void Silent_server_is_silent()
    {
        var now = IntermediaryDns.Judge(Server("comss"), null, "claude.ai");

        Assert.Equal(RelayVerdict.Silent, now.Verdict);
        Assert.Empty(now.Addresses);
    }

    [Fact]
    public void Address_given_to_two_controls_is_the_relay()
    {
        // Так 03.11 отвечал Comss: один прокси на claude.ai и chatgpt.com.
        var answers = Answers(null,
            ("claude.ai", A("206.223.251.18", "45.13.237.213")),
            ("chatgpt.com", A("206.223.251.18")),
            ("grok.com", A("0.0.0.0")));

        var now = IntermediaryDns.Judge(Server("comss"), answers, "claude.ai");

        Assert.Equal(RelayVerdict.Relays, now.Verdict);
        Assert.Equal(["206.223.251.18"], now.Addresses);
    }

    [Fact]
    public void Zapret_dns_relay_comes_from_the_service_name()
    {
        // У Zapret DNS посредник один на всё, и узнаётся он ответом на relay.check.
        var answers = Answers(A("5.129.221.64"),
            ("spotify.com", A("5.129.221.64")));

        var now = IntermediaryDns.Judge(Server("zapret"), answers, "spotify.com");

        Assert.Equal(RelayVerdict.Relays, now.Verdict);
        Assert.Equal(["5.129.221.64"], now.Addresses);
    }

    [Fact]
    public void Real_address_means_not_proxied()
    {
        // Comss 03.11 на gemini.google.com — адрес самого Google, а прокси
        // на контрольных другой: Gemini он не проксирует.
        var answers = Answers(null,
            ("gemini.google.com", A("142.251.157.2")),
            ("claude.ai", A("206.223.251.18")),
            ("chatgpt.com", A("206.223.251.18")));

        // Честный резолвер отдал другой фронт той же сети Google — это настоящий адрес.
        var now = IntermediaryDns.Judge(Server("comss"), answers, "gemini.google.com", A("142.251.140.14"));

        Assert.Equal(RelayVerdict.NotProxied, now.Verdict);
        Assert.Equal(["142.251.157.2"], now.Addresses);
    }

    [Fact]
    public void Unmatched_address_is_unconfirmed_not_refused()
    {
        // Посредник держит сервис на своём узле, а контрольные ИИ больше не проксирует
        // (снимок 01.11: Autodesk у AstraCat на 217.60.179.6). Это не «настоящий адрес».
        var answers = Answers(null,
            ("autodesk.com", A("217.60.179.6")),
            ("claude.ai", A("0.0.0.0")),
            ("chatgpt.com", A("0.0.0.0")));

        var unknown = IntermediaryDns.Judge(Server("astracat"), answers, "autodesk.com");
        var other = IntermediaryDns.Judge(Server("astracat"), answers, "autodesk.com", A("18.155.1.2"));

        Assert.Equal(RelayVerdict.Unconfirmed, unknown.Verdict);
        Assert.Equal(["217.60.179.6"], unknown.Addresses);
        Assert.Equal(RelayVerdict.Unconfirmed, other.Verdict);
    }

    [Fact]
    public void Lost_answer_is_not_a_refusal()
    {
        var answers = Answers(null, ("claude.ai", A())) with
        {
            Unanswered = new HashSet<string>(["claude.ai"], StringComparer.OrdinalIgnoreCase),
        };

        Assert.Equal(RelayVerdict.Unanswered, IntermediaryDns.Judge(Server("comss"), answers, "claude.ai").Verdict);
    }

    [Fact]
    public void Relay_check_is_named_only_when_it_confirmed_the_address()
    {
        var byControls = Answers(null,
            ("claude.ai", A("5.129.221.64")),
            ("chatgpt.com", A("5.129.221.64")));
        var byCheck = Answers(A("5.129.221.64"), ("spotify.com", A("5.129.221.64")));

        Assert.False(IntermediaryDns.Judge(Server("zapret"), byControls, "claude.ai").ByRelayCheck);
        Assert.True(IntermediaryDns.Judge(Server("zapret"), byCheck, "spotify.com").ByRelayCheck);
    }

    [Fact]
    public void Sinkhole_or_empty_answer_refuses()
    {
        var answers = Answers(null, ("grok.com", A("0.0.0.0")));

        Assert.Equal(RelayVerdict.Refuses, IntermediaryDns.Judge(Server("comss"), answers, "grok.com").Verdict);
        Assert.Equal(RelayVerdict.Refuses, IntermediaryDns.Judge(Server("comss"), answers, "unknown.example").Verdict);
    }
}
