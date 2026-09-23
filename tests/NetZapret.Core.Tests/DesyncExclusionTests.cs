using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Что попадает в список имён, которых десинк не касается.
/// </summary>
/// <remarks>
/// Источника два, и оба означают «этому имени вмешательство не нужно»: пин
/// в hosts и правило «напрямую». Прежде собирались только пины, а «напрямую»
/// для winws2 ничем не отличалось от «десинка» — имя всё равно забирала
/// секция пресета и применяла рецепт. Снаружи выглядело так, будто
/// переключатель не работает.
/// </remarks>
public sealed class DesyncExclusionTests : IDisposable
{
    private readonly string _hosts = Path.Combine(
        Path.GetTempPath(), $"netzapret-hosts-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_hosts))
            File.Delete(_hosts);
    }

    private void Write(string content) => File.WriteAllText(_hosts, content);

    /// <summary>«Напрямую» выводит имя из-под десинка по-настоящему.</summary>
    [Fact]
    public void DirectRuleExcludesItsNames()
    {
        Write(string.Empty);

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.discord.media"
                mode: direct
              - match: domain
                value: "*.youtube.com"
                mode: desync
            """);

        var found = HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts);

        Assert.Equal(["discord.media"], found);
    }

    /// <summary>Оба источника складываются в один список, без повторов.</summary>
    [Fact]
    public void PinnedAndDirectAreCollectedTogether()
    {
        Write("72.56.93.144 canva.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.canva.com"
                mode: direct
              - match: domain
                value: "*.discord.media"
                mode: direct
            """);

        var found = HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts);

        Assert.Equal(["canva.com", "discord.media"], found);
    }

    /// <summary>
    /// Перекрытое правило «напрямую» в исключения не идёт.
    /// </summary>
    /// <remarks>
    /// Порядок решает всё: правило может быть перекрыто более ранним, и тогда
    /// имя живёт по чужому режиму. У Discord так и вышло — «обновления» стоят
    /// на «напрямую», а до них имя забирает список сайта, стоящий выше
    /// и покрывающий ту же зону. Записав такое имя в исключения, мы отменили
    /// бы десинк там, где человек его не отменял.
    /// </remarks>
    [Fact]
    public void ShadowedDirectRuleIsIgnored()
    {
        Write(string.Empty);

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.discord.com"
                mode: desync
              - match: domain
                value: "updates.discord.com"
                mode: direct
            """);

        Assert.Empty(HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts));
    }

    /// <summary>
    /// Проксируемые выводятся из-под десинка — и прибитые, и нет.
    /// </summary>
    /// <remarks>
    /// Прежде эта проверка утверждала обратное: «их трафик уходит в туннель,
    /// и десинк его не видит вовсе». Замер 23.09 опроверг — winws2 видит
    /// пакеты, которые приложение шлёт в TUN, и рецепт на поддельных пакетах
    /// рвал Instagram внутри туннеля. См. DesyncBypass.Tunnel.
    /// </remarks>
    [Fact]
    public void ProxiedNamesAreExcludedToo()
    {
        Write("72.56.93.144 canva.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.canva.com"
                mode: proxy
              - match: domain
                value: "*.instagram.com"
                mode: proxy
            """);

        var found = HostsFile.DescribeDesyncExclusions(engine.RuleSet, _hosts);

        Assert.Equal(DesyncBypass.Pin, HostsFile.BypassFor(found, "canva.com"));
        Assert.Equal(DesyncBypass.Tunnel, HostsFile.BypassFor(found, "www.instagram.com"));
    }

    /// <summary>«Десинк» исключением не является — им как раз чинят.</summary>
    [Fact]
    public void DesyncRuleIsNotAnExclusion()
    {
        Write(string.Empty);

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.youtube.com"
                mode: desync
            """);

        Assert.Empty(HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts));
    }

    /// <summary>
    /// Правило по адресам имён не даёт: «напрямую» на подсети исключать
    /// нечего, десинк отбирает трафик по имени в приветствии TLS.
    /// </summary>
    [Fact]
    public void AddressRuleContributesNothing()
    {
        Write(string.Empty);

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: ip
                value: "95.101.173.0/24"
                mode: direct
            """);

        Assert.Empty(HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts));
    }

    /// <summary>
    /// Источник у каждого имени свой, и отчёт его называет.
    /// </summary>
    /// <remarks>
    /// Лечатся они по-разному: пин снимается в разделе «Файл hosts»,
    /// «напрямую» — переключателем в маршрутах. Совет «уберите исключение»
    /// без указания, какое именно, отправляет искать не туда.
    /// </remarks>
    [Fact]
    public void ReasonTellsPinFromDirect()
    {
        Write("72.56.93.144 canva.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.discord.media"
                mode: direct
            """);

        var found = HostsFile.DescribeDesyncExclusions(engine.RuleSet, _hosts);

        Assert.Equal(
            [("canva.com", DesyncBypass.Pin), ("discord.media", DesyncBypass.Direct)],
            found);
    }

    /// <summary>
    /// Имя, и прибитое, и поставленное на «напрямую», числится за пином.
    /// </summary>
    /// <remarks>
    /// Так честнее: пин бьёт резолв независимо от режима, и снимать надо
    /// сперва его. Порядок тот же, в каком имена собираются, — hosts читается
    /// первым.
    /// </remarks>
    [Fact]
    public void PinWinsWhenBothApply()
    {
        Write("72.56.93.144 canva.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.canva.com"
                mode: direct
            """);

        Assert.Equal([("canva.com", DesyncBypass.Pin)], HostsFile.DescribeDesyncExclusions(engine.RuleSet, _hosts));
    }

    /// <summary>
    /// Поддомен исключённой зоны тоже выведен из-под десинка.
    /// </summary>
    /// <remarks>
    /// Список уезжает движку файлом, а файловый список winws2 раскрывает
    /// до поддоменов сам — «subdomains auto apply» в его справке. Дословное
    /// сравнение сказало бы про <c>api.canva.com</c>, что десинк к нему
    /// применяется, тогда как движок не трогает и его.
    /// </remarks>
    [Fact]
    public void BypassCoversSubdomains()
    {
        Write(string.Empty);

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.canva.com"
                mode: direct
            """);

        var found = HostsFile.DescribeDesyncExclusions(engine.RuleSet, _hosts);

        Assert.Equal(DesyncBypass.Direct, HostsFile.BypassFor(found, "api.canva.com"));
        Assert.Equal(DesyncBypass.Direct, HostsFile.BypassFor(found, "canva.com"));

        // Чужая зона, начинающаяся теми же буквами, — не поддомен.
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(found, "notcanva.com"));
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(found, "youtube.com"));
    }
}
