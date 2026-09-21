using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Настройка «игнорировать исключения российских сайтов».
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 21.09, и заводилась она взамен режима «без
/// исключений». Тот отменял ВСЕ прямые правила разом — а у владельца
/// их десять, и ни одно не российское: chatgpt, github, twitch, spotify
/// и прочие выведены напрямую потому, что так работает лучше.
/// </para>
/// <para>
/// Отменять их заодно значило бы делать не то, что написано на настройке.
/// </para>
/// <para>
/// Правила собираются загрузчиком из настоящих файлов, а не руками.
/// Первые две редакции этих проверок строили <see cref="RoutingRule"/>
/// конструктором и краснели: содержимое списка — прочитанные домены
/// и подсети — заполняет загрузчик, и правило без него до конфига
/// не доходит вовсе. Проверка, обошедшая загрузчик, проверяла бы
/// не то, что работает.
/// </para>
/// </remarks>
public sealed class RussianExclusionsTests
{
    private sealed class Lists : IDisposable
    {
        public Lists()
        {
            Root = Path.Combine(Path.GetTempPath(), "nz-ru-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(Root);

            Ours = Write("ipset-ru.txt", "95.108.128.0/17");
            Other = Write("github.txt", "github.com");
        }

        public string Root { get; }

        public string Ours { get; }

        public string Other { get; }

        private string Write(string name, string line)
        {
            var path = Path.Combine(Root, name);

            File.WriteAllText(path, line + Environment.NewLine);

            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // Временный каталог мог быть занят — не беда проверки.
            }
        }
    }

    private static ProxyServer Server() => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Transport = "udp",
        Security = "tls",
        Sni = "nl.example.com",
    };

    private static string Compile(Lists lists, bool ignore, string mode = "proxy")
    {
        var yaml = $"""
            mode: {mode}
            rules:
              - match: ipset
                value: "{lists.Ours.Replace('\\', '/')}"
                mode: direct
              - match: hostlist
                value: "{lists.Other.Replace('\\', '/')}"
                mode: direct
            """;

        var engine = RuleSetLoader.Load(yaml);

        // Содержимое списков подгружает расширитель, а не загрузчик правил.
        // Без него правило по списку до конфига не доходит вовсе, и проверка
        // сравнивала бы два одинаково пустых конфига.
        RuleSetExpander.Expand(engine.RuleSet, zapretRoot: null);

        return new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [Server()],
            new SingBoxOptions { IgnoreRussianExclusions = ignore }).Json;
    }

    [Fact]
    public void The_lists_really_get_read()
    {
        // Первым делом: проверка, обошедшая загрузчик, сравнивала бы
        // два одинаково пустых конфига и зеленела на любом коде.
        using var lists = new Lists();

        var engine = RuleSetLoader.Load($"""
            mode: proxy
            rules:
              - match: ipset
                value: "{lists.Ours.Replace('\\', '/')}"
                mode: direct
            """);

        RuleSetExpander.Expand(engine.RuleSet, zapretRoot: null);

        Assert.NotEmpty(engine.RuleSet.Rules[0].IpSetCidrs);
    }

    [Fact]
    public void By_default_the_russian_networks_stay_direct()
    {
        // Иначе банки и госуслуги увидят зарубежный адрес и начнут требовать
        // подтверждений — та самая беда, ради которой список и ведётся.
        using var lists = new Lists();

        Assert.Contains("95.108.128.0/17", Compile(lists, ignore: false));
    }

    [Fact]
    public void Turned_on_it_drops_exactly_that_rule()
    {
        using var lists = new Lists();

        Assert.DoesNotContain("95.108.128.0/17", Compile(lists, ignore: true));
    }

    [Fact]
    public void The_other_exclusions_survive_it()
    {
        // Ровно то, чем новая настройка отличается от прежнего режима
        // «без исключений»: тот снёс бы и github вместе с остальными
        // девятью, которых человек выводил напрямую по своим причинам.
        using var lists = new Lists();

        Assert.Contains("github.com", Compile(lists, ignore: true));
    }

    [Fact]
    public void In_the_selective_mode_it_changes_nothing()
    {
        // Там в туннель и так уходит лишь названное, и выводить оттуда
        // нечего. Настройка, молча меняющая выборочный режим, удивила бы.
        using var lists = new Lists();

        Assert.Equal(
            Compile(lists, ignore: false, "selective"),
            Compile(lists, ignore: true, "selective"));
    }

    [Theory]
    [InlineData("config/lists/ipset-ru.txt", true)]
    [InlineData("lists/ipset-ru.txt", true)]
    [InlineData("config/lists/ipset-telegram.txt", false)]
    [InlineData("config/lists/ipset-youtube.txt", false)]
    public void Only_that_one_list_is_recognised(string value, bool ours)
    {
        // Опознаётся по имени файла, и потому проверяется отдельно: ошибка
        // тут сняла бы не то правило, а заметить это можно было бы только
        // по сломавшемуся телеграму.
        var rule = new RoutingRule
        {
            Match = MatchKind.IpSet,
            Value = value,
            Mode = RoutingMode.Direct,
        };

        Assert.Equal(ours, SingBoxConfigCompiler.IsRussianNetworks(rule));
    }

    [Fact]
    public void A_hostlist_with_the_same_name_is_not_it()
    {
        // Российские сети заданы подсетями. Список имён с тем же названием —
        // это что-то другое, и снимать его по совпадению имени нельзя.
        var rule = new RoutingRule
        {
            Match = MatchKind.HostList,
            Value = "config/lists/ipset-ru.txt",
            Mode = RoutingMode.Direct,
        };

        Assert.False(SingBoxConfigCompiler.IsRussianNetworks(rule));
    }
}
