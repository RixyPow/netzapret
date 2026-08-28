using System.Net;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// hosts бьёт любой резолв, включая наш, и молча отменяет правила
/// <c>mode: proxy</c>. Так пропали Canva, RuTracker и LinkedIn.
/// </summary>
public sealed class HostsFileTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"netzapret-hosts-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private void Write(string content) => File.WriteAllText(_path, content);

    [Fact]
    public void ReadsAddressesAndSkipsComments()
    {
        Write("""
            # комментарий
            104.21.32.39    rutracker.org www.rutracker.org
            72.56.93.144 canva.com   # хвостовой комментарий
            """);

        var hosts = HostsFile.Read(_path);

        Assert.Equal(IPAddress.Parse("104.21.32.39"), hosts["rutracker.org"][0]);
        Assert.Equal(IPAddress.Parse("104.21.32.39"), hosts["www.rutracker.org"][0]);
        Assert.Equal(IPAddress.Parse("72.56.93.144"), hosts["canva.com"][0]);
    }

    [Fact]
    public void MissingFileIsNotAnError()
    {
        // Ради чтения hosts запуск останавливать незачем.
        Assert.Empty(HostsFile.Read(Path.Combine(Path.GetTempPath(), "нет-такого-файла")));
    }

    [Fact]
    public void SubdomainsCountAsPinned()
    {
        // Правило пишется на зону (*.canva.com), а в hosts перечислены
        // конкретные имена — точное совпадение пропустило бы главное.
        Write("72.56.93.144 static.canva.com");

        var pinned = HostsFile.FindPinned(HostsFile.Read(_path), "canva.com");

        Assert.Equal(IPAddress.Parse("72.56.93.144"), Assert.Single(pinned));
    }

    [Fact]
    public void BlockingEntriesAreNotCaptured()
    {
        // 127.0.0.1 и 0.0.0.0 ставят, чтобы имя не открывалось вовсе.
        // Заводить такое в туннель бессмысленно.
        Write("""
            127.0.0.1 canva.com
            0.0.0.0 rutracker.org
            """);

        var hosts = HostsFile.Read(_path);

        Assert.Empty(HostsFile.FindPinned(hosts, "canva.com"));
        Assert.Empty(HostsFile.FindPinned(hosts, "rutracker.org"));
    }

    [Fact]
    public void PinnedProxyDomainsAreCollectedWithAnExplanation()
    {
        Write("72.56.93.144 canva.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.canva.com"
                mode: proxy
              - match: domain
                value: "*.youtube.com"
                mode: desync
            """);

        var found = HostsFile.CollectPinnedProxyAddresses(engine.RuleSet, out var notes, _path);

        Assert.Equal("72.56.93.144/32", Assert.Single(found));
        Assert.Contains("canva.com", Assert.Single(notes));
    }

    /// <summary>Списочное правило учитывается наравне с доменным.</summary>
    /// <remarks>
    /// Пока учитывались только доменные, это молча не работало для всего,
    /// что назначено через раздел сервисов, — а он пишет именно hostlist.
    /// Так и вышло с ChatGPT: правило говорило «через VPN», в hosts все его
    /// имена были прибиты к давно умолкшему адресу, и трафик уходил туда
    /// мимо туннеля. Со стороны — блокировка, которую ничем не пробить.
    /// </remarks>
    [Fact]
    public void PinnedDomainsOfAHostListRuleAreCollectedToo()
    {
        Write("72.56.93.144 chatgpt.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: hostlist
                value: "lists/chatgpt.txt"
                mode: proxy
            """);

        // Список обычно загружает разворачиватель правил; здесь он подан
        // напрямую, чтобы проверка не зависела от установленного Zapret.
        engine.RuleSet.Rules[0].LoadHostList(["chatgpt.com", "openai.com"]);

        var found = HostsFile.CollectPinnedProxyAddresses(engine.RuleSet, out var notes, _path);

        Assert.Equal("72.56.93.144/32", Assert.Single(found));
        Assert.Contains("chatgpt.com", Assert.Single(notes));
    }

    [Fact]
    public void DesyncDomainsAreLeftAlone()
    {
        // Записи для десинка ставит редактор hosts из Zapret GUI, и уводить
        // их в туннель значило бы сломать то, что работает.
        Write("142.250.1.1 youtube.com");

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.youtube.com"
                mode: desync
            """);

        Assert.Empty(HostsFile.CollectPinnedProxyAddresses(engine.RuleSet, out _, _path));
    }
}
