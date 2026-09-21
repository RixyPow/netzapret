using System.IO;
using System.Windows;
using Microsoft.Win32;
using NetZapret.Core.Rules;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>
/// Книга маршрутов на вкладке: вывоз и ввоз.
/// </summary>
/// <remarks>
/// <para>
/// Пока только это, и намеренно. Книга ещё ничем не управляет — маршруты
/// по-прежнему берутся из правил, — и показать её действующей значило бы
/// соврать: человек правил бы файл и не понимал, почему ничего не меняется.
/// </para>
/// <para>
/// Переносимость она даёт уже сейчас, и ради неё задумывалась: файл
/// «имя: маршрут» не привязан ни к пресету, ни к туннелю, работает
/// с любым из них, и его можно отдать другому человеку.
/// </para>
/// </remarks>
public partial class RoutesView
{
    /// <summary>
    /// Собирает книгу из того, что есть сейчас.
    /// </summary>
    /// <remarks>
    /// Тем же <see cref="RouteBookMigration"/>, которым пользуется nz:
    /// два сборщика одного разошлись бы, и вывезенный файл отличался бы
    /// от показанного.
    /// </remarks>
    private static Migration Current()
    {
        var rules = UserRulesFile.Load().Entries
            .Select(entry => new RoutingRule
            {
                Match = entry.Match,
                Value = entry.Value,
                Mode = entry.Mode,
                Recipe = entry.Recipe,
            })
            .ToList();

        var set = new RuleSet { Rules = rules, DefaultMode = RoutingMode.Desync };

        return RouteBookMigration.From(set, HostsEditor.Pins().Keys.ToList());
    }

    private void ShowBook()
    {
        try
        {
            var made = Current();
            int routes = made.Book.Entries.Count(e => e.Choice != RouteChoice.Pin);
            int pins = made.Book.Entries.Count - routes;

            var line = $"{routes} маршрутов и {pins} пинов.";

            if (made.Kept.Count > 0)
                line += $" Ещё {made.Kept.Count} правил по адресам книгой не выражаются.";

            BookLine.Text = line;

            Clashes.ItemsSource = made.Book.Clashes;
            Clashes.Visibility = made.Book.Clashes.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            BookLine.Text = "не собралась: " + ex.GetBaseException().Message;
            Clashes.Visibility = Visibility.Collapsed;
        }
    }

    private void OnExportBook(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Куда вывезти книгу маршрутов",
            FileName = "routes.yaml",
            Filter = "Книга маршрутов (*.yaml)|*.yaml|Все файлы|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var made = Current();

            File.WriteAllText(dialog.FileName, RouteBookFile.Write(made.Book));

            Status.Text = $"Вывезено: {made.Book.Entries.Count} записей в {dialog.FileName}. "
                + "Нынешние правила и пины не тронуты.";

            this.Offer("Книга маршрутов вывезена");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось вывезти: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Ввозит книгу — со спросом и с перечнем того, что изменится.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашивает, потому что ввоз переписывает работу месяцев: у владельца
    /// восемьдесят восемь правил, подобранных по одному. Молчаливая замена
    /// такого — худшее, что можно сделать с чужим трудом.
    /// </para>
    /// <para>
    /// Правила по адресам при этом сохраняются: книга их не выражает,
    /// и ввоз, отбросивший их, тихо снял бы четыре правила, которые ловят
    /// соединения без имени.
    /// </para>
    /// </remarks>
    private void OnImportBook(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Какую книгу ввезти",
            Filter = "Книга маршрутов (*.yaml)|*.yaml|Все файлы|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var book = RouteBookFile.Parse(File.ReadAllText(dialog.FileName));
            var routes = book.Entries.Count(x => x.Choice != RouteChoice.Pin);
            var pins = book.Entries.Count - routes;

            var kept = Current().Kept;

            var said = $"В книге {routes} маршрутов и {pins} пинов.\n\n"
                + "Нынешние правила будут заменены целиком.";

            if (kept.Count > 0)
                said += $"\n\nПравила по адресам ({kept.Count}) сохранятся: книга их не выражает.";

            if (book.Clashes.Count > 0)
                said += $"\n\nВ книге {book.Clashes.Count} противоречий — их видно ниже после ввоза.";

            said += "\n\nПины при этом не ставятся: строка «pin» — рекомендация, "
                + "и прибивать адреса остаётся отдельным действием.";

            if (MessageBox.Show(said, "Ввезти книгу маршрутов?",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            Apply(book);

            Status.Text = $"Ввезено: {routes} маршрутов. Применится при следующем запуске движков.";

            this.Offer("Книга маршрутов ввезена");

            Reload();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось ввезти: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Записывает книгу поверх пользовательских правил.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Снимаются только те правила, которые книгой выражаются. Правила
    /// по адресам не трогаются вовсе — так их не приходится снимать
    /// и возвращать, а значит негде и потерять.
    /// </para>
    /// <para>
    /// Пины не ставятся: строка «pin» — рекомендация прибить, а не сама
    /// правка. Прибивать адреса в системном файле остаётся отдельным
    /// действием с отдельным согласием, как и было.
    /// </para>
    /// </remarks>
    private static void Apply(RouteBook book)
    {
        var file = UserRulesFile.Load();

        foreach (var entry in file.Entries.ToList())
        {
            var rule = new RoutingRule
            {
                Match = entry.Match,
                Value = entry.Value,
                Mode = entry.Mode,
            };

            if (RouteBookMigration.NameOf(rule) is not null)
                file.Remove(entry.Match, entry.Value);
        }

        foreach (var rule in RouteBookMigration.Back(book))
            file.Set(rule.Match, rule.Value, rule.Mode, rule.Recipe);

        file.Save();
    }
}