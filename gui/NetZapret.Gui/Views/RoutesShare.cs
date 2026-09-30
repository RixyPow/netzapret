using System.IO;
using System.Windows;
using Microsoft.Win32;
using NetZapret.Core.Rules;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>
/// Маршруты файлом на вкладке: сохранить, загрузить, противоречия.
/// </summary>
/// <remarks>
/// <para>
/// До 30.09 здесь была книга маршрутов — свой формат «имя: маршрут»,
/// собиравшийся из правил при сохранении и переводившийся обратно при
/// загрузке. Маршрутами книга не управляла, а перевод отстал: правила
/// для программ при сохранении пропадали, свои списки при загрузке
/// указывали на несуществующий файл. Владелец: делиться самим файлом
/// правил — это одно и то же. Решения — в <see cref="RulesShare"/>,
/// здесь только диалоги.
/// </para>
/// </remarks>
public partial class RoutesView
{
    /// <summary>Сколько строк каждого вида перечислять в вопросе — дальше «и ещё N».</summary>
    private const int ListedInQuestion = 6;

    private void ShowShare()
    {
        try
        {
            var rules = UserRulesFile.Load().Entries;
            var pins = HostsEditor.Pins().Keys.ToList();

            var tip = $"Записать ваши маршруты ({rules.Count}) в файл, чтобы отдать другому человеку. "
                + "Ссылок подписок и ключей в нём нет.";

            if (RulesShare.OnOwnLists(rules) is > 0 and var own)
                tip += $" Правил на свои списки: {own} — их домены в файл не входят.";

            if (pins.Count > 0)
                tip += $" Пины ({pins.Count}) остаются на этой машине.";

            ExportButton.ToolTip = tip;

            var clashes = RouteClashes.FromRules(rules, pins);

            Clashes.ItemsSource = clashes;
            ClashesBox.Visibility = clashes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ExportButton.ToolTip = "Маршруты не прочитались: " + ex.GetBaseException().Message;
            ClashesBox.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSaveRoutes(object sender, RoutedEventArgs e)
    {
        var source = UserRulesFile.DefaultPath;

        if (!File.Exists(source))
        {
            Status.Text = "Своих маршрутов нет — сохранять нечего.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Куда сохранить маршруты",
            FileName = "netzapret-routes.yaml",
            Filter = "Маршруты NetZapret (*.yaml)|*.yaml|Все файлы|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);

            var rules = UserRulesFile.Load().Entries;
            var own = RulesShare.OnOwnLists(rules);

            Status.Text = $"Сохранено: {rules.Count} маршрутов в {dialog.FileName}."
                + (own > 0 ? $" Правил на свои списки: {own} — их домены в файл не вошли." : string.Empty);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сохранить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Загружает чужие маршруты — со спросом и перечнем того, что изменится.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашивает, потому что загрузка заменяет работу месяцев: у владельца
    /// сотня правил, подобранных по одному. Молчаливая замена такого — худшее,
    /// что можно сделать с чужим трудом. Прежде вопрос говорил только
    /// «будут заменены целиком»; теперь — что именно уйдёт и что сменится.
    /// </para>
    /// <para>
    /// Прежний файл перед заменой копируется в runtime: передумал — вернул.
    /// </para>
    /// </remarks>
    private void OnLoadRoutes(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Откуда загрузить маршруты",
            Filter = "Маршруты NetZapret (*.yaml)|*.yaml|Все файлы|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        IReadOnlyList<UserRuleEntry> incoming;

        try
        {
            incoming = RulesShare.Read(dialog.FileName);
        }
        catch (Exception ex)
        {
            Status.Text = "Файл не разобрался — это не маршруты NetZapret? " + ex.GetBaseException().Message;
            return;
        }

        if (incoming.Count == 0)
        {
            Status.Text = "В файле нет ни одного маршрута — загружать нечего.";
            return;
        }

        try
        {
            var current = UserRulesFile.Load().Entries;
            var diff = RulesShare.Compare(current, incoming);

            if (diff.Same)
            {
                Status.Text = "Маршруты в файле совпадают с вашими — менять нечего.";
                return;
            }

            if (MessageBox.Show(Question(diff, incoming), "Загрузить маршруты из файла?",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            var backup = RulesShare.Backup();

            var file = UserRulesFile.Load();
            file.ReplaceAll(incoming);
            file.Save();

            Status.Text = $"Загружено: {incoming.Count} маршрутов. Применится при следующем запуске движков."
                + (backup is null ? string.Empty : $" Прежние — в {backup}.");

            this.Offer("Маршруты загружены");

            Reload();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Вопрос перед заменой: сколько и что именно.</summary>
    private static string Question(RulesDiff diff, IReadOnlyList<UserRuleEntry> incoming)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"Добавится {diff.Added.Count}, уйдёт {diff.Removed.Count}, изменится {diff.Changed.Count}.");

        Listed(text, "Добавится", diff.Added.Select(RulesShare.Describe).ToList());
        Listed(text, "Уйдёт", diff.Removed.Select(RulesShare.Describe).ToList());
        Listed(text, "Изменится", diff.Changed.Select(c => RulesShare.Describe(c.Before, c.After)).ToList());

        // Правило на список, которого здесь нет, не ловит ничего — молча.
        var missing = RulesShare.MissingLists(incoming);

        if (missing.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Списков нет на этой машине ({missing.Count}), и эти правила не будут действовать: "
                + string.Join(", ", missing.Take(ListedInQuestion).Select(RulesShare.NameOf))
                + (missing.Count > ListedInQuestion ? $" и ещё {missing.Count - ListedInQuestion}." : "."));
        }

        text.AppendLine();
        text.Append("Ваши маршруты заменятся целиком; прежний файл сохранится копией в runtime.");

        return text.ToString();
    }

    private static void Listed(System.Text.StringBuilder text, string head, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        text.AppendLine();
        text.AppendLine(head + ":");

        foreach (var line in lines.Take(ListedInQuestion))
            text.AppendLine("  " + line);

        if (lines.Count > ListedInQuestion)
            text.AppendLine($"  и ещё {lines.Count - ListedInQuestion}");
    }
}
