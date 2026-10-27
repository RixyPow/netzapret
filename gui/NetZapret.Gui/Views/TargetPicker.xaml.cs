using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Gui.Views;

/// <summary>Что выбрали для точечной проверки.</summary>
/// <param name="Title">Как назвать выбранное: папка, маршрут или своё имя.</param>
/// <param name="Routes">Маршруты «Маршрутов» — ключ и подпись строк таблицы; пусто — своё имя.</param>
/// <param name="Host">Своё имя; <c>null</c> — выбраны маршруты.</param>
internal sealed record CheckTarget(string Title, IReadOnlyList<(string Key, string Label)> Routes, string? Host)
{
    public string Describe => Title;
}

/// <summary>Строка выбора: папка «Маршрутов» или маршрут в ней.</summary>
internal sealed record PickItem(string Title, string Detail, bool IsFolder, string? Folder, IReadOnlyList<(string Key, string Label)> Routes)
{
    public Thickness Indent => IsFolder || Folder is null ? new Thickness(0) : new Thickness(22, 0, 0, 0);

    public FontWeight Weight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;

    public Visibility DetailShown => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Выбор цели для точечной проверки.
/// </summary>
/// <remarks>
/// <para>
/// Отдельным окном, потому что прежде глубина и сервисы лежали в одном
/// выпадающем списке: две глубины сверху, а под ними два десятка сервисов
/// одной лентой. Выбор глубины и выбор цели — разные вопросы, и слитый
/// список отвечал сразу на оба, заставляя пролистывать чужое.
/// </para>
/// <para>
/// С 27.10 список — папки и маршруты «Маршрутов» в их порядке (владелец:
/// «в точечной проверке должен быть выбор из папок/маршрутов с вкладки
/// маршруты»), а не справочник сервисов: свои папки, программы и свои
/// домены справочник не знал, и проверить их точечно было нельзя.
/// </para>
/// </remarks>
public partial class TargetPicker : Window
{
    internal CheckTarget? Chosen { get; private set; }

    private readonly IReadOnlyList<PickItem> _all;

    public TargetPicker()
    {
        InitializeComponent();

        _all = Build();
        Items.ItemsSource = _all;
    }

    /// <summary>
    /// Папки и маршруты в порядке «Маршрутов» — той же раскладкой (RouteFolders)
    /// и теми же ключами строк (RouteKeys), что и сам раздел.
    /// </summary>
    /// <remarks>
    /// Только то, что проверяется по имени: части по адресам, подсети, программы
    /// по процессу и «Адреса» программ здесь не видны — стучаться в подсеть наугад
    /// не проверка. Реестр блокировок — тоже мимо: его первые записи — мёртвые
    /// домены, в отчёте от них был бы только шум.
    /// </remarks>
    private static IReadOnlyList<PickItem> Build()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        IReadOnlyList<UserRuleEntry> own;

        try
        {
            own = UserRulesFile.Load().Entries;
        }
        catch (Exception)
        {
            // Свои правила не прочитались — справочник от этого не теряет смысла.
            own = [];
        }

        // Ключ строки → её подпись; null — строка есть, но по имени не проверяется.
        var placed = new List<(string? Folder, string Key, string? Title)>();

        placed.AddRange(settings.MadeFolders.Select(name => ((string?)name, "folder|" + name, (string?)null)));

        foreach (var program in settings.ProgramRoutes)
        {
            placed.Add((program.Program, RouteKeys.Make(RouteKeys.HostList, ProgramRoutes.DomainsPath(program.Program)), "Домены"));
            placed.Add((program.Program, RouteKeys.Make(RouteKeys.HostList, ProgramRoutes.AddressesPath(program.Program)), null));
        }

        foreach (var service in ServiceCatalog.All)
        {
            if (service.Parts.Count == 0)
                continue;

            foreach (var part in service.Parts)
            {
                var key = RouteKeys.Make(part.ByAddress ? RouteKeys.IpSet : RouteKeys.HostList, part.List);
                var title = part.ByAddress || part.Bulk ? null : part.Name;

                // Как в «Маршрутах»: полка разных сервисов раскладывается сразу,
                // сервис из одной части папкой не становится и зовётся своим именем.
                if (service.Grouping)
                    placed.Add((null, key, title));
                else if (service.Parts.Count == 1)
                    placed.Add((null, key, title is null ? null : service.Name));
                else
                    placed.Add((service.Name, key, title));
            }
        }

        foreach (var entry in own)
        {
            if (entry.Match == MatchKind.Domain
                || (entry.Match == MatchKind.HostList && OwnLists.IsOwn(entry.Value) && !ProgramRoutes.IsProgramList(entry.Value)))
            {
                var title = entry.Match == MatchKind.HostList ? OwnLists.NameOf(entry.Value) : entry.Value.TrimStart('*', '.');
                placed.Add((null, RouteKeys.Make(RouteKeys.Own, entry.Value), title));
            }
        }

        var ordered = RouteFolders.Order(placed.Select(p => (p.Folder, p.Key, p)).ToList(), settings.RouteOrder);
        var items = new List<PickItem>();

        foreach (var (folder, all) in RouteFolders.Arrange(ordered, settings.RouteFolders))
        {
            var routes = all.Where(p => p.Title is not null).ToList();

            if (routes.Count == 0)
                continue;

            if (folder is null)
            {
                foreach (var route in routes)
                    items.Add(new PickItem(route.Title!, string.Empty, false, null, [(route.Key, route.Title!)]));

                continue;
            }

            var labelled = routes.Select(r => (r.Key, $"{folder} · {r.Title}")).ToList();

            items.Add(new PickItem(folder, Count(routes.Count), true, folder, labelled));

            foreach (var route in routes)
                items.Add(new PickItem(route.Title!, string.Empty, false, folder, [(route.Key, $"{folder} · {route.Title}")]));
        }

        return items;
    }

    private static string Count(int n)
    {
        int last = n % 10, lastTwo = n % 100;

        var word = last == 1 && lastTwo != 11 ? "маршрут"
            : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "маршрута"
            : "маршрутов";

        return $"{n} {word}";
    }

    /// <summary>
    /// Набранное — и своё имя, и поиск: список сужается до подходящих папок
    /// и маршрутов, а не выбрано ничего — проверяется набранное имя.
    /// </summary>
    private void OnOwnChanged(object sender, TextChangedEventArgs e)
    {
        var text = Own.Text.Trim();

        Items.ItemsSource = text.Length == 0
            ? _all
            : _all.Where(i => i.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (i.Folder?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        UpdateAccept();
    }

    private void OnItemChosen(object sender, SelectionChangedEventArgs e) => UpdateAccept();

    private void OnItemConfirmed(object sender, MouseButtonEventArgs e)
    {
        if (Items.SelectedItem is not null)
            OnAccept(sender, e);
    }

    private void UpdateAccept()
    {
        Accept.IsEnabled = Items.SelectedItem is not null || Own.Text.Trim().Length > 0;
        Accept.Content = Items.SelectedItem is PickItem { IsFolder: true } ? "Проверить папку" : "Проверить";
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Items.SelectedItem is PickItem item)
        {
            Chosen = new CheckTarget(item.IsFolder || item.Folder is null ? item.Title : $"{item.Folder} · {item.Title}", item.Routes, null);
            DialogResult = true;
            return;
        }

        // Имя, а не строка как есть: 19.10 владелец вставил ссылку
        // «https://www.star-history.com/», проба искала адрес у неё целиком
        // и получила «нет адреса у имени» — а итог при этом «всё открыто».
        // Разбор тот же, что у поля «Маршрутов»; не похожее на имя (адрес)
        // уходит как было.
        var own = DomainInput.Normalize(Own.Text) ?? Own.Text.Trim();

        Chosen = own.Length > 0 ? new CheckTarget(own, [], own) : null;
        DialogResult = Chosen is not null;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
