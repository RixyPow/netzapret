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
}
