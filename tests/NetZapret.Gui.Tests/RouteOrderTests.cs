using System.Windows.Media;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Раскладка списка маршрутов, в том числе по методу.
/// </summary>
/// <remarks>
/// Просьба владельца 21.09. Порядок каталога отвечает на «что ломается
/// чаще», алфавит — на «где тут Zoom», а метод на третий вопрос: «что
/// у меня вообще идёт через VPN». Перебирать ради него семьдесят строк
/// и читать подпись у каждой — ровно та работа, которую делают за человека.
/// </remarks>
public sealed class RouteOrderTests
{
    private static PartRow Part(int choice) => new()
    {
        Key = "hostlist|список",
        Title = "часть",
        Detail = "часть",
        Mode = "режим",
        Color = Brushes.Transparent,
        Choice = choice,
        Applied = choice,
        CanRoute = true,
        CanPin = true,
        Letter = "Ч",
    };

    private static ServiceRow Service(string name, params int[] choices) =>
        new(name, choices.Select(Part).ToList());

    /// <summary>0 — напрямую, 1 — десинк, 2 — через VPN.</summary>
    private static readonly ServiceRow[] Rows =
    [
        Service("Ябло", 2),
        Service("Апельсин", 1),
        Service("Банан", 0),
        Service("Вишня", 2),
    ];

    [Fact]
    public void The_catalogue_order_is_left_alone()
    {
        // Сверху то, что ломается чаще, — порядок не случаен и трогать
        // его без просьбы нельзя.
        var order = RouteOrder.Apply(Rows, RouteOrderBy.Catalog);

        Assert.Equal(Rows.Select(r => r.Name), order.Select(r => r.Name));
    }

    [Fact]
    public void By_name_is_by_name()
    {
        var order = RouteOrder.Apply(Rows, RouteOrderBy.Name);

        Assert.Equal(["Апельсин", "Банан", "Вишня", "Ябло"], order.Select(r => r.Name));
    }

    [Fact]
    public void By_method_groups_and_then_sorts_inside()
    {
        // Порядок методов тот же, что в списке выбора у каждой строки:
        // напрямую, десинк, через VPN. Внутри группы — по алфавиту,
        // иначе там остаётся порядок каталога и искать не легче.
        var order = RouteOrder.Apply(Rows, RouteOrderBy.Method);

        Assert.Equal(["Банан", "Апельсин", "Вишня", "Ябло"], order.Select(r => r.Name));
    }

    [Fact]
    public void A_service_goes_by_its_prevailing_method()
    {
        // У Discord пять частей, и одна выбивающаяся не должна уводить
        // его из своей группы.
        var mixed = Service("Смешанный", 1, 1, 1, 2);

        Assert.Equal(1, RouteOrder.MethodOf(mixed));
    }

    [Fact]
    public void A_tie_goes_to_the_more_intervening()
    {
        // Поровну — значит сервис настроен наполовину, и видеть его
        // уместнее среди настроенных, чем среди нетронутых.
        Assert.Equal(2, RouteOrder.MethodOf(Service("Пополам", 0, 2)));
        Assert.Equal(1, RouteOrder.MethodOf(Service("Тоже", 0, 1)));
    }

    [Fact]
    public void A_service_without_parts_does_not_throw()
    {
        // Пустых быть не должно, но раскладка — не то место, где стоит
        // падать из-за этого.
        Assert.Equal(0, RouteOrder.MethodOf(new ServiceRow("Пустой", [])));
    }

    [Fact]
    public void The_method_is_named_the_same_way_everywhere()
    {
        // Те же слова, что в списке выбора у строки. Свои слова здесь
        // заставляли бы вспоминать, какое из них что значит.
        Assert.Equal("напрямую", RouteOrder.Name(0));
        Assert.Equal("десинк", RouteOrder.Name(1));
        Assert.Equal("через VPN", RouteOrder.Name(2));
    }

    [Fact]
    public void The_dropdown_and_the_enum_agree()
    {
        // Выбор берётся по номеру пункта списка, и разойдись они —
        // «по методу» раскладывало бы по алфавиту, молча.
        Assert.Equal(0, (int)RouteOrderBy.Catalog);
        Assert.Equal(1, (int)RouteOrderBy.Name);
        Assert.Equal(2, (int)RouteOrderBy.Method);
    }
}
