using System.Text.RegularExpressions;
using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Карточка «О программе» не расходится с разбором лицензий.
/// </summary>
/// <remarks>
/// Два списка одного состава: <c>docs/THIRD-PARTY.md</c> для читающего
/// репозиторий и <see cref="About.Components"/> для того, кто видит только
/// окно. Новый движок попадёт в первый при разборе лицензии — и молча
/// не попадёт во второй, если его не сверять.
/// </remarks>
public sealed class AboutTests
{
    private static string? ThirdParty()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return Path.Combine(directory.FullName, "docs", "THIRD-PARTY.md");
        }

        return null;
    }

    /// <summary>Каждый раздел о чужом компоненте есть и в карточке.</summary>
    [Fact]
    public void EveryThirdPartySectionIsListed()
    {
        var path = ThirdParty();
        if (path is null || !File.Exists(path))
            return;

        var sections = Regex.Matches(File.ReadAllText(path), @"^## (.+?)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            // Два раздела о самом NetZapret, а не о составе.
            .Where(s => !s.Contains("NetZapret"))
            .ToList();

        Assert.NotEmpty(sections);

        foreach (var section in sections)
        {
            // «wintun.dll» в разборе и «Wintun» в карточке — один компонент.
            var word = section.Split('.', ' ')[0];

            Assert.True(
                About.Components.Any(c => c.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase)),
                $"«{section}» есть в THIRD-PARTY.md, но нет в карточке «О программе»");
        }
    }

    [Fact]
    public void LinksAreHttps()
    {
        var links = About.Components.Select(c => c.Source)
            .Concat(About.Components.Select(c => c.Site).OfType<string>())
            .Concat([About.Repository, About.Issues, About.Discussions, About.Telegram, About.Support]);

        Assert.All(links, link => Assert.StartsWith("https://", link));
    }

    /// <summary>
    /// Zapret GUI назван отдельно от Zapret 2, с автором и его сайтом.
    /// </summary>
    /// <remarks>
    /// До 30.09 всё, что едет рядом с winws2, — сценарии lua, списки, пресеты —
    /// было приписано bol-van. Владелец: «нам нужно оставить на него ссылку
    /// как авторское право того, что он сделал».
    /// </remarks>
    [Fact]
    public void ZapretGuiIsCreditedToItsAuthor()
    {
        var gui = Assert.Single(About.Components, c => c.Name == "Zapret GUI");

        Assert.Contains("loop-uh", gui.Role);
        Assert.Equal("https://wiki.zapret.moe/", gui.Site);
        Assert.StartsWith("https://git.zapret.moe/", gui.Source);

        // И списки с пресетами больше не числятся за Zapret 2.
        var zapret = Assert.Single(About.Components, c => c.Name == "Zapret 2");

        Assert.DoesNotContain("списки", zapret.Role);
        Assert.DoesNotContain("пресеты", zapret.Role);
    }

    /// <summary>
    /// Обе лицензии Zapret едут в архив: уведомление об авторстве — условие MIT.
    /// </summary>
    [Fact]
    public void BothZapretNoticesAreShipped()
    {
        var path = ThirdParty();
        if (path is null)
            return;

        var root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        var pack = File.ReadAllText(Path.Combine(root, "pack.cmd"));

        foreach (var notice in new[] { "zapret-MIT.txt", "zapretgui-MIT.txt" })
        {
            Assert.True(File.Exists(Path.Combine(root, "docs", "licenses", notice)), $"нет docs/licenses/{notice}");
            Assert.Contains(notice, pack);
        }

        Assert.Contains(
            "Copyright (c) 2025-2026 censorliber",
            File.ReadAllText(Path.Combine(root, "docs", "licenses", "zapretgui-MIT.txt")));
    }

    /// <summary>
    /// Ссылка на репозиторий и проверка обновлений смотрят в одно место:
    /// после переезда проекта карточка вела бы на старый адрес.
    /// </summary>
    [Fact]
    public void RepositoryMatchesUpdateCheck()
    {
        Assert.EndsWith("/" + UpdateCheck.Repository, About.Repository);
    }
}
