using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>Задачи поиска «Что нужно сделать?» ведут в существующие разделы.</summary>
public sealed class TaskCatalogTests
{
    /// <summary>
    /// Раздел задачи — <c>Tag</c> пункта меню. Переименовали пункт, а задачу забыли —
    /// выбор в поиске молча никуда бы не вёл.
    /// </summary>
    [Fact]
    public void Every_task_opens_a_section_of_the_menu()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Xaml", "MainWindow.xaml"));
        var tags = Regex.Matches(xaml, @"Style=""\{StaticResource RailItem\}""[^>]*?Tag=""(?<tag>\w+)""")
            .Select(m => m.Groups["tag"].Value)
            .ToHashSet();

        Assert.NotEmpty(tags);

        foreach (var task in TaskCatalog.All)
            Assert.True(tags.Contains(task.Section), $"«{task.Text}»: раздела «{task.Section}» в меню нет");
    }

    /// <summary>У каждого раздела меню есть хоть одна задача — иначе поиск о нём не знает.</summary>
    [Fact]
    public void Every_section_has_a_task()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Xaml", "MainWindow.xaml"));
        var tags = Regex.Matches(xaml, @"Style=""\{StaticResource RailItem\}""[^>]*?Tag=""(?<tag>\w+)""")
            .Select(m => m.Groups["tag"].Value);

        foreach (var tag in tags)
            Assert.Contains(TaskCatalog.All, t => t.Section == tag);
    }
}
