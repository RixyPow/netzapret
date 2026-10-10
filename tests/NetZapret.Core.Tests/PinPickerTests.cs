using Microsoft.Data.Sqlite;
using NetZapret.Proxy;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Автоподбор адреса для пина: кого берём и что говорим.
/// </summary>
/// <remarks>
/// Сеть здесь не трогается — проверяется порядок выбора и чтение каталога.
/// Живой прогон 23.09: crunchyroll.com получил XBOX DNS 87.228.47.195
/// (301 из Стокгольма), chatgpt.com — 87.228.47.204 (Франкфурт),
/// image.tmdb.org и rutracker.org — свои настоящие адреса.
/// </remarks>
public sealed class PinPickerTests
{
    private static PinProbe Probe(string address, PinSource source, PinVerdict verdict, string? colo = null, int ms = 100) =>
        new(new PinCandidate(address, source, source.ToString()), verdict,
            verdict is PinVerdict.Works ? 200 : verdict is PinVerdict.Dead ? null : 403,
            colo, TimeSpan.FromMilliseconds(ms), verdict.ToString());

    private static PinProbe Best(params PinProbe[] probes) =>
        probes.Where(p => p.Usable).OrderBy(p => p.Rank).First();

    /// <summary>Сайт ответил сам по настоящему адресу — посредник не нужен.</summary>
    [Fact]
    public void HonestThatWorksBeatsAnIntermediary()
    {
        var best = Best(
            Probe("9.9.9.9", PinSource.Pool, PinVerdict.Works, "ARN", ms: 50),
            Probe("1.1.1.1", PinSource.Honest, PinVerdict.Works, "DME", ms: 300));

        Assert.Equal(PinSource.Honest, best.Candidate.Source);
    }

    /// <summary>Ответ сайта лучше проверки на робота, откуда бы тот ни был.</summary>
    [Fact]
    public void WorksBeatsChallenge()
    {
        var best = Best(
            Probe("1.1.1.1", PinSource.Catalog, PinVerdict.Challenge, "FRA"),
            Probe("9.9.9.9", PinSource.Pool, PinVerdict.Works, "ARN"));

        Assert.Equal("9.9.9.9", best.Candidate.Address);
    }

    /// <summary>
    /// Проверка на робота по настоящему адресу — всегда с нашего, российского
    /// адреса, даже если ответил узел в Стокгольме. Посредник с той же
    /// проверкой лучше.
    /// </summary>
    [Fact]
    public void HonestChallengeLosesToIntermediaryChallenge()
    {
        var best = Best(
            Probe("1.1.1.1", PinSource.Honest, PinVerdict.Challenge, "ARN"),
            Probe("9.9.9.9", PinSource.Pool, PinVerdict.Challenge, "FRA", ms: 900));

        Assert.Equal("9.9.9.9", best.Candidate.Address);
    }

    /// <summary>Отказ по стране и молчание не выбираются никогда.</summary>
    [Fact]
    public void RefusedAndDeadAreNeverUsable()
    {
        Assert.False(Probe("1.1.1.1", PinSource.Honest, PinVerdict.Refused).Usable);
        Assert.False(Probe("9.9.9.9", PinSource.Pool, PinVerdict.Dead).Usable);
    }

    /// <summary>
    /// Переадресация подбирается, только если ведёт на тот же сайт:
    /// crunchyroll.com → www.crunchyroll.com — да; vrv.co → crunchyroll — нет,
    /// это уже чужое имя, и прибивать его без спроса не нам.
    /// </summary>
    [Theory]
    [InlineData("crunchyroll.com", "www.crunchyroll.com", true)]
    [InlineData("www.crunchyroll.com", "crunchyroll.com", true)]
    [InlineData("vrv.co", "www.crunchyroll.com", false)]
    public void RedirectIsFollowedWithinTheSite(string from, string to, bool same)
    {
        Assert.Equal(same, PinPicker.SameSite(from, to));
    }

    /// <summary>
    /// Из кэша DNS берутся только имена под зонами сервиса — не всё,
    /// что машина спрашивала.
    /// </summary>
    [Fact]
    public void CachedNamesAreTakenUnderServiceZones()
    {
        var names = DnsCache.Under(
            ["sso.crunchyroll.com", "beta-api.crunchyroll.com", "Crunchyroll.com", "notcrunchyroll.com", "www.google.com"],
            ["crunchyroll.com", "*.vrv.co"]);

        Assert.Equal(["beta-api.crunchyroll.com", "crunchyroll.com", "sso.crunchyroll.com"], names);
    }

    /// <summary>Чтение кэша не падает: функция недокументированная.</summary>
    [Fact]
    public void ReadingTheCacheDoesNotThrow()
    {
        var names = DnsCache.Names();

        Assert.NotNull(names);
    }

    [Fact]
    public void NothingFoundSaysTunnel()
    {
        var picks = new[]
        {
            new PinPick("crunchyroll.com", null, [Probe("1.1.1.1", PinSource.Honest, PinVerdict.Refused)]),
        };

        Assert.Contains("туннель", PinPicker.Summarize(picks, "crunchyroll.com"));
    }

    /// <summary>
    /// Отвергнутые сводятся по источнику и причине: четыре адреса честного
    /// резолвера с одним отказом — одна строка, а не четыре.
    /// </summary>
    [Fact]
    public void SummaryGroupsRejections()
    {
        var chosen = Probe("87.228.47.195", PinSource.Pool, PinVerdict.Works, "ARN");
        var rejected = Enumerable.Range(1, 4)
            .Select(i => Probe($"104.18.34.{i}", PinSource.Honest, PinVerdict.Refused, "DME"))
            .ToList();

        var text = PinPicker.Summarize([new PinPick("crunchyroll.com", chosen, rejected)], "crunchyroll.com");

        Assert.Contains("crunchyroll.com → 87.228.47.195", text);
        Assert.Contains("(4 адр.)", text);
        Assert.Contains("Подобрано для всех имён: 1", text);
    }

    /// <summary>
    /// Посредник — адрес за многими именами; настоящий адрес сети доставки
    /// за парой имён в пул не попадает.
    /// </summary>
    [Fact]
    public void CatalogIntermediariesAreAddressesBehindManyNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netzapret-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "system"));
        var path = Path.Combine(root, ZapretCatalog.RelativePath);

        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();

                command.CommandText = """
                    create table services(service_id text primary key, name text, category text, kind text);
                    create table dns_profiles(profile_id text primary key, name text);
                    create table domains(domain_id integer primary key, service_id text, hostname text);
                    create table dns_answers(domain_id integer, profile_id text, ip_address text, priority integer default 0);
                    create table hosts_entries(entry_id integer primary key, service_id text, hostname text, ip_address text, priority integer default 0);
                    insert into services values('s', 'S', 'ai', 'dns');
                    insert into dns_profiles values('xbox', 'XBOX DNS');
                    """;
                command.ExecuteNonQuery();

                for (int i = 0; i < 25; i++)
                {
                    command.CommandText = $"""
                        insert into domains values({i}, 's', 'n{i}.example');
                        insert into dns_answers(domain_id, profile_id, ip_address) values({i}, 'xbox', '{(i < 2 ? "23.32.25.53" : "87.228.47.195")}');
                        """;
                    command.ExecuteNonQuery();
                }
            }

            var catalog = ZapretCatalog.Discover(root)!;

            var pool = catalog.Intermediaries();
            Assert.Equal(["87.228.47.195"], pool.Select(p => p.Address));
            Assert.All(pool, p => Assert.Equal(PinSource.Pool, p.Source));

            var answers = catalog.AnswersFor("n0.example");
            Assert.Equal([("23.32.25.53", "XBOX DNS")], answers.Select(a => (a.Address, a.Label)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Без IPv6 в сети каталог отдаёт имени первый IPv4, а не первый по приоритету.
    /// </summary>
    /// <remarks>
    /// У instagram.com в разделе «Напрямую» первыми стоят два адреса IPv6,
    /// и пин на них в сети без IPv6 мёртв (владелец 06.10: «делай учет ipv6»).
    /// </remarks>
    [Fact]
    public void WithoutIpV6TheCatalogGivesTheFirstIpV4()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netzapret-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "system"));
        var path = Path.Combine(root, ZapretCatalog.RelativePath);

        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();

                command.CommandText = """
                    create table services(service_id text primary key, name text, category text, kind text);
                    create table dns_profiles(profile_id text primary key, name text);
                    create table domains(domain_id integer primary key, service_id text, hostname text);
                    create table dns_answers(domain_id integer, profile_id text, ip_address text, priority integer default 0);
                    create table hosts_entries(entry_id integer primary key, service_id text, hostname text, ip_address text, priority integer default 0);
                    insert into services values('hosts.instagram', 'Instagram', 'direct', 'hosts');
                    insert into hosts_entries(service_id, hostname, ip_address, priority) values
                        ('hosts.instagram', 'instagram.com', '2a03:2880:f330:25:face:b00c:0:4420', 2),
                        ('hosts.instagram', 'instagram.com', '163.70.151.174', 4);
                    """;
                command.ExecuteNonQuery();
            }

            var catalog = ZapretCatalog.Discover(root)!;

            Assert.Equal("2a03:2880:f330:25:face:b00c:0:4420", catalog.Answers(["hosts.instagram"], null)["instagram.com"]);
            Assert.Equal("163.70.151.174", catalog.Answers(["hosts.instagram"], null, ipV6: false)["instagram.com"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Переадресация на страницу отказа — отказ по стране, а не ответ сайта.
    /// </summary>
    /// <remarks>
    /// 10.10 настоящий адрес claude.ai отвечал
    /// <c>302 → claude.com/app-unavailable-in-region</c>, и подбор принял его.
    /// </remarks>
    [Theory]
    [InlineData("https://claude.com/app-unavailable-in-region", true)]
    [InlineData("https://example.com/unsupported_country", true)]
    [InlineData("https://example.com/error?reason=not-available-in-your-country", true)]
    [InlineData("https://www.crunchyroll.com/", false)]
    [InlineData("https://claude.com/login", false)]
    [InlineData("https://region-unavailable.example.com/", false)]
    public void RedirectToARegionRefusalIsARefusal(string location, bool refusal)
    {
        Assert.Equal(refusal, PinPicker.IsRegionRefusal(new Uri(location)));
    }

    private static PinPick Pick(string host, params PinProbe[] probes) =>
        new(host, null, probes);

    /// <summary>
    /// Все имена сайта — на один адрес, даже если другому имени лучше
    /// подошёл бы другой посредник.
    /// </summary>
    /// <remarks>
    /// 10.10 у Claude было три источника разом: claude.ai — настоящий адрес,
    /// frame.claudeusercontent.com — XBOX, downloads.claude.ai — Comss.
    /// </remarks>
    [Fact]
    public void TheWholeSiteGoesToOneAddress()
    {
        var picks = PinPicker.OneAddress(
        [
            Pick("claude.ai",
                Probe("160.79.104.10", PinSource.Honest, PinVerdict.Refused),
                Probe("95.81.102.20", PinSource.Catalog, PinVerdict.Challenge, "AMS", ms: 1500),
                Probe("188.68.214.131", PinSource.Catalog, PinVerdict.Dead)),
            Pick("frame.claudeusercontent.com",
                Probe("188.68.214.131", PinSource.Catalog, PinVerdict.Works, ms: 50),
                Probe("95.81.102.20", PinSource.Catalog, PinVerdict.Works, ms: 400)),
        ], "claude.ai");

        Assert.All(picks, p => Assert.Equal("95.81.102.20", p.Chosen?.Candidate.Address));
    }

    /// <summary>
    /// Имя, которому адрес сайта не годится, остаётся без пина, а не уходит
    /// к другому посреднику.
    /// </summary>
    [Fact]
    public void ANameTheSiteAddressFailsStaysUnpinned()
    {
        var picks = PinPicker.OneAddress(
        [
            Pick("claude.ai", Probe("95.81.102.20", PinSource.Catalog, PinVerdict.Challenge, "AMS")),
            Pick("downloads.claude.com",
                Probe("95.81.102.20", PinSource.Catalog, PinVerdict.Refused),
                Probe("193.233.112.68", PinSource.Catalog, PinVerdict.Works)),
        ], "claude.ai");

        Assert.Equal("95.81.102.20", picks[0].Chosen?.Candidate.Address);
        Assert.Null(picks[1].Chosen);
        Assert.Equal(2, picks[1].Rejected.Count);
    }

    /// <summary>
    /// Адрес, которому не отвечает главное имя, сайт не получит, сколько бы
    /// прочих имён он ни покрыл.
    /// </summary>
    [Fact]
    public void TheMainNameDecides()
    {
        var picks = PinPicker.OneAddress(
        [
            Pick("site.example",
                Probe("1.1.1.1", PinSource.Pool, PinVerdict.Dead),
                Probe("2.2.2.2", PinSource.Pool, PinVerdict.Works)),
            Pick("a.site.example", Probe("1.1.1.1", PinSource.Pool, PinVerdict.Works)),
            Pick("b.site.example", Probe("1.1.1.1", PinSource.Pool, PinVerdict.Works)),
        ], "site.example");

        Assert.Equal("2.2.2.2", picks[0].Chosen?.Candidate.Address);
        Assert.Null(picks[1].Chosen);
        Assert.Null(picks[2].Chosen);
    }

    /// <summary>
    /// Из годных главному имени выигрывает тот, кто покрывает больше имён.
    /// </summary>
    [Fact]
    public void AmongAddressesTheMainAcceptsTheWiderWins()
    {
        var picks = PinPicker.OneAddress(
        [
            Pick("site.example",
                Probe("1.1.1.1", PinSource.Pool, PinVerdict.Works, ms: 50),
                Probe("2.2.2.2", PinSource.Pool, PinVerdict.Works, ms: 300)),
            Pick("cdn.site.example",
                Probe("1.1.1.1", PinSource.Pool, PinVerdict.Dead),
                Probe("2.2.2.2", PinSource.Pool, PinVerdict.Works)),
        ], "site.example");

        Assert.All(picks, p => Assert.Equal("2.2.2.2", p.Chosen?.Candidate.Address));
    }

    /// <summary>
    /// Настоящие адреса — один источник: у каждого имени свой адрес, но сайт
    /// целиком идёт без посредника.
    /// </summary>
    [Fact]
    public void HonestAddressesCountAsOneSource()
    {
        var picks = PinPicker.OneAddress(
        [
            Pick("site.example",
                Probe("10.0.0.1", PinSource.Honest, PinVerdict.Works),
                Probe("9.9.9.9", PinSource.Pool, PinVerdict.Works)),
            Pick("cdn.site.example",
                Probe("10.0.0.2", PinSource.Honest, PinVerdict.Works),
                Probe("9.9.9.9", PinSource.Pool, PinVerdict.Dead)),
        ], "site.example");

        Assert.Equal("10.0.0.1", picks[0].Chosen?.Candidate.Address);
        Assert.Equal("10.0.0.2", picks[1].Chosen?.Candidate.Address);
    }

    /// <summary>
    /// Неудачный подбор объясняет себя в журнале: кто и чем отказал.
    /// </summary>
    /// <remarks>
    /// 26.09 журнал писал «адрес не подобран, проверено 12» — и выяснить
    /// задним числом, почему не нашёлся рабочий прежде адрес Crunchyroll,
    /// было нечем.
    /// </remarks>
    [Fact]
    public void A_failed_pick_says_why_grouped_by_source()
    {
        var pick = new PinPick("www.crunchyroll.com", null,
        [
            Probe("1.1.1.1", PinSource.Honest, PinVerdict.Dead),
            Probe("1.1.1.2", PinSource.Honest, PinVerdict.Dead),
            Probe("45.155.204.190", PinSource.Pool, PinVerdict.Refused),
        ]);

        var text = PinPicker.Rejections(pick);

        Assert.Contains("Honest — Dead (2 адр.)", text);
        Assert.Contains("Pool — отказ 403", text);
        Assert.Equal("кандидатов не было", PinPicker.Rejections(new PinPick("x.example", null, [])));
    }
}
