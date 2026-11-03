using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Один маршрут или папка файлом (03.11): «Экспорт…» в «⋯» строки и папки,
/// загрузка — «Добавить правило» → «Из файла…».
/// </summary>
/// <remarks>
/// <para>
/// Загрузка — общей кнопкой, а не «Импортом…» в «⋯» папки: что заменить,
/// решает файл, в нём папка названа. «Импорт…» в меню Discord, открывший
/// файл с YouTube, обещал бы одно, а делал другое. И «Загрузить» внизу,
/// получив файл одного маршрута, загружает его так же — а не заменяет им
/// все маршруты.
/// </para>
/// <para>
/// Решения — в <see cref="RouteBundles"/>, здесь только диалоги.
/// </para>
/// </remarks>
public partial class RoutesView
{
    private const string BundleFilter = "Маршрут NetZapret (*.yaml)|*.yaml|Все файлы|*.*";

    /// <summary>«Экспорт…» у папки: все её маршруты, а не только найденные поиском.</summary>
    private void ExportFolder(ServiceRow folder)
    {
        var row = _all.FirstOrDefault(s => s.Name == folder.Name && s.IsFolder) ?? folder;
        Export(row.Parts, row.Name, row.Shown);
    }

    /// <summary>«Экспорт…» у строки — маршрут сам по себе, без папки.</summary>
    private void ExportRoute(PartRow part) => Export([part], null, part.Title);

    private void Export(IReadOnlyList<PartRow> parts, string? folder, string shown)
    {
        var routes = new List<(MatchKind, string)>();

        foreach (var part in parts)
        {
            if (RouteKeys.Parse(part.Key) is var (kind, value))
                routes.Add((RouteKeys.MatchOf(kind, value), value));
        }

        RouteBundle bundle;

        try
        {
            bundle = RouteBundles.Export(routes, folder, UserRulesFile.Load(), AppSettings.Load(AppSettings.DefaultPath));
        }
        catch (Exception ex)
        {
            Status.Text = "Маршруты не прочитались: " + ex.GetBaseException().Message;
            return;
        }

        // Старое правило по программе и прочее, чего файлом не перенести, — не пустым файлом.
        if (folder is null && bundle.Routes.Count == 0)
        {
            Status.Text = $"«{shown}» файлом не переносится.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = folder is null ? $"Куда сохранить маршрут «{shown}»" : $"Куда сохранить папку «{shown}»",
            FileName = FileName(shown),
            Filter = BundleFilter,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            RouteBundles.Write(bundle, dialog.FileName);

            Status.Text = $"Сохранено: {bundle.Caption}, {Count(bundle.Routes.Count, "маршрут", "маршрута", "маршрутов")} — {dialog.FileName}. "
                + "Свои списки — в файле, пины — нет."
                + (bundle.Left > 0
                    ? $" Не вошло: {bundle.Left} — строки списков, которые не имя и не адрес, или маршрут, которого не перенести."
                    : string.Empty);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сохранить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Имя файла по имени маршрута: знаки, которых Windows в имени не пускает, — дефисом.</summary>
    private static string FileName(string shown)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(shown.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();

        return $"NetZapret — {(name.Length == 0 ? "маршрут" : name)}.yaml";
    }

    /// <summary>«Добавить правило» → «Из файла…».</summary>
    private void ImportFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Маршрут или папка из файла",
            Filter = BundleFilter,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ImportBundle(dialog.FileName);
    }

    /// <summary>
    /// Загружает пакет — со спросом и перечнем: что появится, что заменится, чего не будет.
    /// </summary>
    /// <remarks>
    /// Спрашивает, как и «Загрузить» всех маршрутов: заменяется выбор человека,
    /// и молча менять его на присланный нельзя. Прежние правила и списки —
    /// копией в runtime.
    /// </remarks>
    private void ImportBundle(string path)
    {
        RouteBundle bundle;

        try
        {
            bundle = RouteBundles.Read(path);
        }
        catch (Exception ex)
        {
            Status.Text = $"Файл не загрузился: {ex.GetBaseException().Message}. Файл всех маршрутов — кнопкой «Загрузить» внизу.";
            return;
        }

        try
        {
            var preset = _preset;
            var plan = RouteBundles.Plan(bundle, UserRulesFile.Load(), AppSettings.Load(AppSettings.DefaultPath),
                recipeKnown: preset is null ? null : name => RecipeResolver.Known(preset, name));

            if (plan.Steps.Count > 0 && plan.Steps.All(s => s.Action == RouteImportAction.Skip))
            {
                Status.Text = $"Из файла не загрузится ничего: {plan.Steps[0].Note}.";
                return;
            }

            if (plan.Nothing)
            {
                Status.Text = $"В файле — {bundle.Caption}, и у вас всё так же: менять нечего.";
                return;
            }

            if (MessageBox.Show(Window.GetWindow(this), BundleQuestion(plan), "Загрузить маршрут из файла?",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            var result = RouteBundles.Apply(plan);

            if (bundle.Folder is { } folder)
                _open.Add(folder);

            Reload();

            Status.Text = $"Загружено: {bundle.Caption} — добавлено {result.Added}, заменено {result.Replaced}. "
                + "Применится при следующем запуске движков."
                + (result.Backup is null ? string.Empty : $" Прежнее — в {result.Backup}.");

            this.Offer("Маршруты загружены");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Вопрос перед загрузкой: что именно и с какими оговорками.</summary>
    private static string BundleQuestion(RouteImportPlan plan)
    {
        var text = new StringBuilder();
        var steps = plan.Steps;

        int Of(RouteImportAction action) => steps.Count(s => s.Action == action);

        text.AppendLine($"{Capital(plan.Bundle.Caption)} из файла: {Count(steps.Count, "маршрут", "маршрута", "маршрутов")}.");

        if (plan.NewProgram)
            text.AppendLine("Такой программы у вас нет — появится со своими списками.");
        else if (plan.NewFolder)
            text.AppendLine("Такой папки у вас нет — появится.");

        text.AppendLine($"Добавится {Of(RouteImportAction.Add)}, заменится {Of(RouteImportAction.Replace)}, "
            + $"уже так {Of(RouteImportAction.Same)}, не загрузится {Of(RouteImportAction.Skip)}.");

        Listed(text, "Добавится", Lines(RouteImportAction.Add));
        Listed(text, "Заменится", Lines(RouteImportAction.Replace));
        Listed(text, "Не загрузится", Lines(RouteImportAction.Skip));

        Listed(text, "Обратите внимание", steps
            .Where(s => s.Action != RouteImportAction.Skip && s.Note is not null)
            .Select(s => $"{s.Name}: {s.Note}")
            .ToList());

        if (plan.Moves > 0 && !plan.NewFolder)
        {
            text.AppendLine();
            text.AppendLine($"В папку переложится: {Count(plan.Moves, "маршрут", "маршрута", "маршрутов")}.");
        }

        if (plan.Staying > 0)
        {
            text.AppendLine();
            text.AppendLine($"В этой папке у вас есть ещё {Count(plan.Staying, "маршрут", "маршрута", "маршрутов")}, "
                + "которых нет в файле, — они останутся как есть.");
        }

        text.AppendLine();
        text.Append("Прежние правила и заменяемые списки сохранятся копией в runtime.");

        return text.ToString();

        IReadOnlyList<string> Lines(RouteImportAction action) =>
            steps.Where(s => s.Action == action).Select(s => s.Describe()).ToList();
    }

    private static string Capital(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
