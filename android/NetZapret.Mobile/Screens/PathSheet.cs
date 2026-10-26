using Avalonia.Controls;
using Avalonia.Media;
using NetZapret.Core.Rules;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>Выбор пути в листе: что выбрали.</summary>
internal enum PathPick
{
    Default,
    Direct,
    Desync,
    Proxy,
    Off,
    Remove,
}

/// <summary>
/// Лист выбора пути маршрута — карточки окна «Путь» Windows (PathWindow).
/// </summary>
/// <remarks>
/// «Блокировать» здесь нет: в модели правил такого пути нет вовсе
/// (<see cref="RoutingMode"/> — напрямую, VPN, десинк), есть «Выключено» —
/// правило не действует. Блок потребовал бы нового режима и в библиотеке,
/// и в сборке конфига (ui.md §3.3, §5 п. 8).
/// </remarks>
internal static class PathSheet
{
    /// <param name="current">Что стоит сейчас — подсвечивается.</param>
    /// <param name="defaultLabel">Куда пойдёт без своего правила; <c>null</c> — пункта «По умолчанию» нет.</param>
    /// <param name="removable">Своё правило можно убрать целиком (свой сайт, приложение).</param>
    public static void Show(
        MainView shell,
        string title,
        PathPick current,
        string? defaultLabel,
        bool removable,
        bool offOffered,
        Action<PathPick> chosen)
    {
        Sheet? sheet = null;
        var list = Ui.Stack(8);

        void Option(PathPick pick, string name, string hint, string colour)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
            grid.Children.Add(Ui.Dot(Ui.Brush(colour), 12));

            var texts = Ui.Stack(2, Ui.Text(name, "h3"), Ui.Text(hint, "muted", "small"));
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            var button = new Button { Content = grid, Classes = { "option" } };

            if (pick == current)
                button.Classes.Add("selected");

            button.Click += (_, _) =>
            {
                sheet?.Close();

                if (pick != current)
                    chosen(pick);
            };

            list.Children.Add(button);
        }

        if (defaultLabel is not null)
            Option(PathPick.Default, "По умолчанию", $"Как задано заводскими правилами: {defaultLabel}", "Faint");

        Option(PathPick.Direct, "Напрямую", "Мимо VPN и без десинка — обычным путём телефона", "Muted");
        Option(PathPick.Desync, "Десинк", "Напрямую, а закрытое по имени чинит десинк. Адрес домашний", "Accent");
        Option(PathPick.Proxy, "Через VPN", "Через туннель: адрес зарубежный, лечит и отказы по стране", "Info");

        if (offOffered)
            Option(PathPick.Off, "Выключено", "Не действует ни своё правило, ни заводское", "Faint");

        if (removable)
            Option(PathPick.Remove, "Убрать маршрут", "Своё правило пропадёт, путь решат остальные", "Danger");

        sheet = shell.ShowSheet(title, list);
    }

    public static PathPick Of(RoutingMode mode, bool off) => off ? PathPick.Off : mode switch
    {
        RoutingMode.Proxy => PathPick.Proxy,
        RoutingMode.Desync => PathPick.Desync,
        _ => PathPick.Direct,
    };

    public static RoutingMode ModeOf(PathPick pick) => pick switch
    {
        PathPick.Proxy => RoutingMode.Proxy,
        PathPick.Desync => RoutingMode.Desync,
        _ => RoutingMode.Direct,
    };

    public static string Word(PathPick pick) => pick switch
    {
        PathPick.Proxy => "VPN",
        PathPick.Desync => "десинк",
        PathPick.Off => "выключено",
        _ => "напрямую",
    };

    /// <summary>Пилюля пути в строке — цветом, как точки в листе.</summary>
    public static Border Pill(string text, RoutingMode mode, bool off)
    {
        var colour = off ? "Faint" : mode switch
        {
            RoutingMode.Proxy => "Info",
            RoutingMode.Desync => "Accent",
            _ => "Muted",
        };

        return Ui.Chip(text, Ui.Brush(colour), Ui.Brush(colour));
    }

    public static IBrush Colour(RoutingMode mode) => Ui.Brush(mode switch
    {
        RoutingMode.Proxy => "Info",
        RoutingMode.Desync => "Accent",
        _ => "Muted",
    });
}
