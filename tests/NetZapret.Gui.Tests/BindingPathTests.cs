using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Каждый путь привязки в разметке есть у какой-нибудь строки.
/// </summary>
/// <remarks>
/// <para>
/// Сторожит отказ, который ничем себя не выдаёт. Опечатка в имени стиля
/// роняет разбор шаблона — её ловит <see cref="ResourceKeyTests"/>.
/// Опечатка в имени свойства не роняет ничего: WPF пишет жалобу в отладочный
/// вывод, которого никто не видит, и оставляет у цели значение по умолчанию.
/// </para>
/// <para>
/// Тихо — не значит безобидно. <c>Visibility</c> по умолчанию видимый,
/// и промах в <c>RemoveShown</c> показал бы кнопку «убрать» у всех семидесяти
/// каталожных строк, где убирать нечего. <c>IsEnabled</c> по умолчанию
/// разрешает: промах в <c>CanPin</c> включил бы пин у частей, заданных
/// подсетями, — а hosts понимает только имена.
/// </para>
/// <para>
/// Сверяется со свойствами живых типов, а не со вторым списком имён:
/// список рядом пришлось бы править вместе с кодом, и забытая правка
/// сделала бы проверку зелёной ровно тогда, когда она нужна.
/// </para>
/// </remarks>
public sealed class BindingPathTests
{
    /// <summary>Простые пути: <c>{Binding Имя}</c> и <c>{Binding Path=Имя}</c>.</summary>
    /// <remarks>
    /// Составные (<c>A.B</c>), индексаторы и привязки с источником не берутся:
    /// у них цель определяется не типом строки, и проверять их так нельзя.
    /// Пустой <c>{Binding}</c> — привязка к самому объекту, проверять нечего.
    /// </remarks>
    private static readonly Regex Simple = new(
        @"\{Binding\s+(?:Path=)?(?<name>[A-Za-z_]\w*)\s*\}",
        RegexOptions.Compiled);

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Xaml");

    /// <summary>
    /// Все свойства типов, к которым окно привязывается.
    /// </summary>
    /// <remarks>
    /// Не только своих. С 21.09 разметка привязывается и к типам библиотек:
    /// карточка книги маршрутов показывает <c>RouteClash</c> — противоречие,
    /// найденное в правилах, — а живёт он в <c>NetZapret.Core</c>. Проверка,
    /// знающая одну сборку окна, объявляла такую привязку опечаткой.
    ///
    /// Шире набор — слабее проверка, и это цена. Но выбор между «ложная
    /// жалоба на верную привязку» и «пропущенная опечатка среди чужих
    /// имён» решается в пользу второго: ложная жалоба заставляет обходить
    /// проверку, а обойдённая проверка не ловит уже ничего.
    /// </remarks>
    private static HashSet<string> Known()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        var assemblies = new[]
        {
            typeof(NetZapret.Gui.Views.PartRow).Assembly,
            typeof(NetZapret.Core.Rules.RouteClash).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    names.Add(property.Name);
                }
            }
        }

        return names;
    }

    [Fact]
    public void The_row_types_are_where_the_test_expects_them()
    {
        // Пустой набор имён сделал бы проверку ниже зелёной, не проверив
        // ничего: не нашлось бы ни одного промаха, потому что не с чем
        // сверять.
        var known = Known();

        Assert.Contains("RemoveShown", known);
        Assert.Contains("ChevronAngle", known);
        Assert.True(known.Count > 50, $"свойств найдено {known.Count}");
    }

    [Fact]
    public void Every_bound_property_exists()
    {
        var known = Known();
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(Folder, "*.xaml"))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Simple.Matches(text))
            {
                var name = match.Groups["name"].Value;

                if (!known.Contains(name))
                    missing.Add($"{Path.GetFileName(file)}: {name}");
            }
        }

        Assert.Empty(missing);
    }
}
