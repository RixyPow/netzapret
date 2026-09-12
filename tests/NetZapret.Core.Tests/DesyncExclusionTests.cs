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
    /// Проксируемые не идут ни из того источника, ни из другого: их трафик
    /// уходит в туннель, и десинк его не видит вовсе.
    /// </summary>
    [Fact]
    public void ProxiedNamesStayOut()
    {
        Write("72.56.93.144 canva.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.canva.com"
                mode: proxy
            """);

        Assert.Empty(HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts));
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
}
