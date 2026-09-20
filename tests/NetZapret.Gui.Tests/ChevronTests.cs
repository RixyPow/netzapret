using System.IO;
using System.Windows;
using System.Windows.Media;
using Xunit;

// Полным именем: в этом файле «Path» уже занято System.IO.Path,
// которым ищутся файлы разметки.
using Arrow = System.Windows.Shapes.Path;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Стрелка сворачивания — одна на всё окно и нарисована фигурой.
/// </summary>
/// <remarks>
/// <para>
/// Поводом была жалоба 20.09: «стрелочки кривые». Причина оказалась не
/// в состоянии, а в шрифте. Подписки рисовали пару «►/▼» — U+25BA
/// и U+25BC, оба в Segoe UI есть. Маршруты и хосты рисовали «▸/▾» —
/// U+25B8 и U+25BE, которых в Segoe UI нет вовсе, и система подменяла
/// их первым попавшимся шрифтом с такими знаками. Один и тот же жест
/// на двух вкладках выходил разной гарнитурой: разный размер, разный
/// вес, разная высота над строкой.
/// </para>
/// <para>
/// Проверок две, и обе нужны. Первая сторожит рисунок: знаки вернуться
/// не должны — любые, потому что предсказать, какой из них окажется
/// в шрифте следующей Windows, нельзя. Вторая сторожит единство: пять
/// раскрывающихся карточек, каждая со своим мнением, уже разошлись.
/// </para>
/// </remarks>
public sealed class ChevronTests
{
    /// <summary>
    /// Знаки, на которые нельзя возвращаться.
    /// </summary>
    /// <remarks>
    /// Оба начертания правого треугольника: «►» U+25BA стоял в подписках,
    /// «▶» U+25B6 — его сосед по таблице, и подменить одно другим при
    /// правке проще простого.
    /// </remarks>
    private static readonly char[] Triangles = ['►', '▶', '▼', '▸', '▾'];

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Xaml");

    [Fact]
    public void Shut_points_right_and_open_points_down()
    {
        // Фигура в стиле нарисована смотрящей вправо, поэтому
        // свёрнутому состоянию поворот не нужен вовсе.
        Assert.Equal(0, Chevrons.Angle(open: false));
        Assert.Equal(90, Chevrons.Angle(open: true));
    }

    [Fact]
    public void No_view_draws_a_chevron_with_a_font_glyph()
    {
        var found = new List<string>();

        foreach (var file in Directory.GetFiles(Folder, "*.xaml"))
        {
            // Без примечаний: там эти знаки стоят намеренно — ими
            // объяснено, почему их здесь больше нет.
            var text = Undocumented(File.ReadAllText(file));

            foreach (var sign in Triangles)
            {
                if (text.Contains(sign))
                    found.Add($"{Path.GetFileName(file)}: {sign} (U+{(int)sign:X4})");
            }
        }

        Assert.Empty(found);
    }

    [Fact]
    public void Every_collapsible_card_uses_the_shared_shape()
    {
        var uses = Directory.GetFiles(Folder, "*.xaml")
            .Sum(f => Count(File.ReadAllText(f), "{StaticResource Chevron}"));

        // Пять раскрывающихся карточек: каталог рецептов, порядок
        // вычисления, чужие записи, папка сервиса, карточка подписки.
        // «Свой домен» отсюда выбыл 20.09 — он больше не сворачивается.
        Assert.Equal(5, uses);
    }

    [Fact]
    public void Turning_the_arrow_leaves_the_shared_style_alone()
    {
        // Преобразование из стиля заморожено — стиль один на все стрелки
        // окна. Правка на месте уронила бы приложение, и падала бы она
        // у человека при первом же щелчке, а не здесь.
        Sta.Run(() =>
        {
            var controls = new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/NetZapret;component/Theme/Controls.xaml",
                    UriKind.Absolute),
            };

            var style = Assert.IsType<Style>(controls["Chevron"]);
            Assert.Equal(typeof(Arrow), style.TargetType);

            var arrow = new Arrow { Style = style };
            Chevrons.Turn(arrow, open: true);

            var turn = Assert.IsType<RotateTransform>(arrow.RenderTransform);
            Assert.Equal(90, turn.Angle);
            Assert.False(turn.IsFrozen);

            Chevrons.Turn(arrow, open: false);
            Assert.Equal(0, ((RotateTransform)arrow.RenderTransform).Angle);
        });
    }

    /// <summary>Разметка без примечаний <c>&lt;!-- --&gt;</c>.</summary>
    private static string Undocumented(string xaml) =>
        System.Text.RegularExpressions.Regex.Replace(
            xaml, "<!--.*?-->", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

    private static int Count(string text, string needle)
    {
        int n = 0, at = 0;

        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }

        return n;
    }
}
