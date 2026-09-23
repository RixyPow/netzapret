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
            .Concat([About.Repository, About.Issues, About.Discussions, About.Telegram, About.Support]);

        Assert.All(links, link => Assert.StartsWith("https://", link));
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
