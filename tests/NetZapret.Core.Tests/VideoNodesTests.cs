using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Зарубежные узлы видео YouTube (10.11): итог замера и почерк. Сам замер ходит
/// в сеть и здесь не гоняется — проверяется то, что из его чисел выводится.
/// </summary>
public sealed class VideoNodesTests
{
    private static VideoNode Node(
        string group,
        int tcp,
        int quic,
        NodeFilter filter = NodeFilter.GoogleOnly,
        int quicTries = VideoNodes.Tries) => new()
        {
            Host = $"rr1---sn-{group}{VideoNodes.Zone}",
            Address = "173.194.10.38",
            Tries = VideoNodes.Tries,
            Tcp = tcp,
            QuicTries = quicTries,
            Quic = quic,
            Filter = filter,
        };

    private static VideoNodesReport Report(params VideoNode[] nodes) => new() { Nodes = nodes, Desync = true };

    /// <summary>Картина 10.11 у владельца: фильтр на узлах, через обход проходит около четырёх пятых.</summary>
    [Fact]
    public void AFifthOfLostConnectionsIsASignature()
    {
        var report = Report(
            Node("a", 3, 2), Node("b", 2, 3), Node("c", 3, 2), Node("d", 2, 2),
            Node("e", 3, 3, NodeFilter.Open), Node("f", 3, 3, NodeFilter.Open));

        Assert.Equal(VideoNodesVerdict.Lossy, report.Verdict);

        var finding = Assert.Single(Signatures.Find([], video: report));

        Assert.Same(Signatures.VideoNodesLossy, finding.Signature);
        Assert.False(finding.Calm);
        Assert.Contains("открываются не всегда: TCP 16 из 18, QUIC 15 из 18, под фильтром 4 из 6 узлов", finding.Headline);
        Assert.Equal(6, finding.Lines.Count);
        Assert.StartsWith("rr1---sn-a (173.194.10.38) — TCP 3 из 3, QUIC 2 из 3; мимо обхода — только имени Google", finding.Lines[0]);
    }

    /// <summary>Одна потеря из тридцати — случай, а не почерк.</summary>
    [Fact]
    public void ASingleLostConnectionIsNotLoss()
    {
        var nodes = Enumerable.Range(0, 10).Select(i => Node($"n{i}", 3, 3, NodeFilter.Open)).ToArray();
        nodes[0] = nodes[0] with { Tcp = 2 };

        var report = Report(nodes);

        Assert.Equal(VideoNodesVerdict.Fine, report.Verdict);
        Assert.Empty(Signatures.Find([], video: report));
    }

    /// <summary>Фильтр стоит, но обход справляется — сведения, а не поломка.</summary>
    [Fact]
    public void AFilterThatTheBypassBeatsIsCalm()
    {
        var report = Report(Node("a", 3, 3), Node("b", 3, 3), Node("c", 3, 3), Node("d", 3, 3, NodeFilter.Open));

        Assert.Equal(VideoNodesVerdict.FilteredButPasses, report.Verdict);
        Assert.True(Assert.Single(Signatures.Find([], video: report)).Calm);
    }

    /// <summary>Без обхода своё имя на узле под фильтром не проходит вовсе — и это сказано словами.</summary>
    [Fact]
    public void WithoutDesyncTheReportSaysItIsTheBareNetwork()
    {
        var report = Report(Node("a", 0, 0), Node("b", 0, 1), Node("c", 0, 0), Node("d", 0, 0)) with { Desync = false };

        Assert.Equal(VideoNodesVerdict.Closed, report.Verdict);
        Assert.Contains("не открываются (десинк не работал — это сеть без обхода): TCP 0 из 12", report.Summary);
    }

    /// <summary>Узлы без адреса и заведённые в туннель в счёт не идут; мало измеренных — судить не по чему.</summary>
    [Fact]
    public void NodesWithoutAnAddressOrInTheTunnelAreNotCounted()
    {
        var report = Report(
            Node("a", 0, 0), Node("b", 0, 0), Node("c", 0, 0),
            new VideoNode { Host = "rr1---sn-gone" + VideoNodes.Zone },
            new VideoNode { Host = "rr1---sn-vpn" + VideoNodes.Zone, Address = "198.18.0.7", Tunnelled = true });

        Assert.Equal(3, report.Measured.Count);
        Assert.Equal(VideoNodesVerdict.NotMeasured, report.Verdict);
        Assert.Null(report.Summary);
        Assert.Empty(Signatures.Find([], video: report));
        Assert.Equal("rr1---sn-gone — нет адреса у имени", report.Lines[3]);
        Assert.Equal("rr1---sn-vpn — имя заведено в туннель, мимо него не мерилось", report.Lines[4]);
    }

    /// <summary>Все узлы в туннеле — так и сказано, а не тишина.</summary>
    [Fact]
    public void AllNodesInTheTunnelSaySo()
    {
        var report = Report(Enumerable.Range(0, 4)
            .Select(i => new VideoNode { Host = $"rr1---sn-n{i}{VideoNodes.Zone}", Address = "198.18.0.7", Tunnelled = true })
            .ToArray());

        Assert.Contains("заведены в туннель", report.Summary);
    }

    /// <summary>Без QUIC на машине он не считается потерей, а род фильтра без проб мимо обхода не называется.</summary>
    [Fact]
    public void MissingQuicAndUnknownFilterAreLeftOut()
    {
        var report = Report(Enumerable.Range(0, 4)
            .Select(i => Node($"n{i}", 3, 0, NodeFilter.Unknown, quicTries: 0))
            .ToArray());

        Assert.Equal(VideoNodesVerdict.Fine, report.Verdict);
        Assert.Equal("TCP 12 из 12", report.Totals);
        Assert.Equal("rr1---sn-n0 (173.194.10.38) — TCP 3 из 3", report.Lines[0]);
    }

    /// <summary>При массовом отказе сети узлы видео, как и прочие почерки, не ищутся.</summary>
    [Fact]
    public void AMassFailureSilencesVideoNodesToo()
    {
        var dead = Enumerable.Range(0, 12).Select(i => new TargetReport
        {
            Host = $"h{i}.example",
            Tcp = new ProbeOutcome { Ok = false },
            Tls = new ProbeOutcome { Ok = false },
            Http = new ProbeOutcome { Ok = false },
            Data = new ProbeOutcome { Ok = false },
            Kind = BlockKind.Full,
        }).ToList();

        var found = Signatures.Find(dead, video: Report(Node("a", 0, 0), Node("b", 0, 0), Node("c", 0, 0), Node("d", 0, 0)));

        Assert.Same(Signatures.MassFailure, Assert.Single(found).Signature);
    }

    /// <summary>Точечная проверка другого сервиса узлы видео не трогает.</summary>
    [Fact]
    public void OnlyARunThatTouchesYoutubeMeasuresTheNodes()
    {
        Assert.True(VideoNodes.Concerns(["discord.com", "www.youtube.com"]));
        Assert.True(VideoNodes.Concerns(["rr4---sn-nx8xon3t-83vl.googlevideo.com"]));
        Assert.True(VideoNodes.Concerns(["youtube.com"]));
        Assert.False(VideoNodes.Concerns(["discord.com", "notyoutube.com", "googlevideo.com.example"]));
        Assert.False(VideoNodes.Concerns([]));
    }

    /// <summary>В наборе — узлы зоны googlevideo, без повторов.</summary>
    [Fact]
    public void TheNodeSetIsDistinctAndInTheZone()
    {
        Assert.Equal(VideoNodes.Hosts.Count, VideoNodes.Hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(VideoNodes.Hosts, h => Assert.EndsWith(VideoNodes.Zone, h));
        Assert.True(VideoNodes.Hosts.Count >= VideoNodes.NodeFloor);
    }

    /// <summary>
    /// Отчёт для разбора (11.11): по узлу — задержка, время каждой попытки и потолок
    /// с малым окном; ответ, опоздавший на 0,2 с против двух задержек, помечен.
    /// </summary>
    [Fact]
    public void TheDetailCarriesPingTimesAndTheSmallWindowCeiling()
    {
        var node = Node("a", 2, 3) with { PingMs = 50, TcpMs = [104, null, 347], QuicMs = [60, 62, 61] };

        Assert.Equal(1, node.Lagged);
        Assert.True(node.Far);
        Assert.Equal(10, Math.Round(node.CeilingMbit!.Value));

        var line = node.Detail();

        Assert.Contains("пинг 50 мс", line);
        Assert.Contains("TCP 2 из 3: 104, нет, 347 мс, с задержкой 1", line);
        Assert.Contains("QUIC 3 из 3: 60, 62, 61 мс", line);
        Assert.Contains("до ~10 Мбит/с", line);
    }

    /// <summary>Сводка делит узлы порогом рецепта и говорит, как пойдёт плеер: QUIC у части узлов молчит.</summary>
    [Fact]
    public void TheDetailsSplitNodesByPingAndSayWhereQuicIsSilent()
    {
        var near = Node("a", 3, 3) with { PingMs = 22, TcpMs = [60, 61, 62], QuicMs = [30, 30, 30] };
        var far = Node("b", 3, 0) with { PingMs = 44, TcpMs = [100, 100, 100], QuicMs = [null, null, null] };
        var cache = Node("cache", 3, 3, NodeFilter.Unknown) with { Cache = true, PingMs = 2 };

        var text = string.Join("\n", (Report(near, far) with { CacheNode = cache, Recipe = "Пресет: X." }).Details);

        Assert.StartsWith("Пресет: X.", text);
        Assert.Contains("Кэш провайдера: rr1---sn-cache", text);
        Assert.Contains("ближние (пинг до 35 мс): узлов 1, пинг 22 мс", text);
        Assert.Contains("дальние (пинг больше 35 мс): узлов 1, пинг 44 мс", text);
        Assert.Contains("у 1 из 2 узлов он не прошёл ни разу", text);

        // Кэш провайдера — не зарубежный узел: потолка малого окна у него нет и в счёт он не идёт.
        Assert.DoesNotContain("с малым окном", cache.Detail());
        Assert.Equal(6, (Report(near, far) with { CacheNode = cache }).Tries);
    }

    /// <summary>Узел без замера задержки и кэш, который не определился, отчёт не роняют.</summary>
    [Fact]
    public void TheDetailsSurviveMissingPingAndCache()
    {
        var text = string.Join("\n", (Report(Node("a", 0, 0)) with { CacheNote = "не определён — Google не назвал узел" }).Details);

        Assert.Contains("Кэш провайдера: не определён", text);
        Assert.Contains("пинг не измерен", text);
        Assert.Contains("не соединились ни разу (пинг не измерен): узлов 1", text);
    }

    /// <summary>
    /// Площадка Google вместо кэша провайдера (11.11, Тула и СПб): отчёт говорит, что
    /// всё видео идёт с зарубежных узлов, а не называет её кэшем провайдера.
    /// </summary>
    [Fact]
    public void AGoogleSiteIsNotCalledTheProvidersCache()
    {
        Assert.True(VideoNodes.IsGoogleSite("fra16s69"));
        Assert.True(VideoNodes.IsGoogleSite("arn09s18"));
        Assert.False(VideoNodes.IsGoogleSite("sevensky-vko2"));
        Assert.Equal("fra16s69 (Франкфурт)", VideoNodes.SiteName("fra16s69"));

        var site = Node("x", 3, 3, NodeFilter.Unknown) with
        {
            Host = "rr1---fra16s69" + VideoNodes.Zone, Cache = true, GoogleSite = true, PingMs = 43,
        };

        var text = string.Join("\n", (Report(Node("a", 0, 0)) with { CacheNode = site }).Details);

        Assert.Contains("Кэша Google у провайдера нет", text);
        Assert.Contains("fra16s69 (Франкфурт)", text);
        Assert.DoesNotContain("Кэш провайдера:", text);
    }
}
