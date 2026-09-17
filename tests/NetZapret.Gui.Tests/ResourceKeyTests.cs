using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Каждая кисть и каждый стиль, на который ссылается разметка, существует.
/// </summary>
/// <remarks>
/// <para>
/// Дополняет <see cref="ViewsLoadTests"/> там, где тот бессилен. Созданием
/// вкладки проверяется лишь то, что разбирается сразу; содержимое
/// <c>DataTemplate</c> разбирается в мгновение, когда в списке появится первый
/// элемент, — то есть уже у человека и уже с данными. Опечатка в имени стиля
/// внутри шаблона строки проходит и сборку, и создание вкладки, и падает
/// ровно тогда, когда проверка что-то нашла.
/// </para>
/// <para>
/// Поэтому здесь читается текст разметки, а не собранное дерево: ссылки
/// берутся из файлов, а существующие имена — из живых словарей темы. Второе
/// важно: сверять текст с текстом значило бы проверять, что мы дважды
/// написали одно и то же, а не что оно работает.
/// </para>
/// </remarks>
public sealed class ResourceKeyTests
{
    /// <summary>Ссылки вида <c>{StaticResource Имя}</c> и <c>{DynamicResource Имя}</c>.</summary>
    /// <remarks>
    /// Только простые имена. Ссылки на системные ключи записываются через
    /// <c>{x:Static}</c> и под это не подходят — и правильно: их существование
    /// обеспечивает не наша тема.
    /// </remarks>
    private static readonly Regex Reference = new(
        @"\{(?:Static|Dynamic)Resource\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex Declaration = new(
        @"x:Key=""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Xaml");

    /// <summary>Имена, объявленные темами.</summary>
    /// <remarks>
    /// Берутся из словарей, поднятых по-настоящему: так учитывается и то,
    /// что одна тема достраивает другую.
    /// </remarks>
    private static HashSet<string> Defined()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        Sta.Run(() =>
        {
            foreach (var theme in new[] { "Palette", "Controls", "Light" })
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/NetZapret;component/Theme/{theme}.xaml",
                        UriKind.Absolute),
                };

                foreach (var key in dictionary.Keys)
                {
                    if (key is string name)
                        names.Add(name);
                }
            }
        });

        return names;
    }

    [Fact]
    public void The_xaml_is_where_the_test_expects_it()
    {
        // Отдельной проверкой: пустая папка сделала бы все остальные
        // проверки этого файла зелёными, ничего не проверив.
        Assert.True(Directory.Exists(Folder), Folder);
        Assert.True(Directory.GetFiles(Folder, "*.xaml").Length >= 12);
    }

    [Fact]
    public void Every_referenced_style_and_brush_exists()
    {
        var themed = Defined();
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(Folder, "*.xaml"))
        {
            var text = File.ReadAllText(file);

            // Имена, объявленные в самом файле: шаблон строки, локальная
            // кисть, свой стиль кнопки. Они не в теме и быть там не должны.
            var own = Declaration.Matches(text)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match match in Reference.Matches(text))
            {
                var name = match.Groups[1].Value;

                if (!themed.Contains(name) && !own.Contains(name))
                    missing.Add($"{Path.GetFileName(file)}: {name}");
            }
        }

        Assert.Empty(missing);
    }
}
