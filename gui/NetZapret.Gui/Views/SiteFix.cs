using System.Windows;
using System.Windows.Controls;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// «Сайт не открывается?» — подбор маршрута по симптому с ходом и итогом.
/// </summary>
/// <remarks>
/// Одно на «Ещё» и простую «Главную» (12.11): владелец попросил, чтобы
/// «Починить» на «Главной» запускала тот же автоподбор, что в «Ещё». Прежде
/// весь ход жил в разделе «Ещё»; копия на «Главной» разошлась бы с ним
/// в первой же правке итога.
/// </remarks>
internal static class SiteFix
{
    private const string Reveal = "Показать в «Маршрутах»";

    /// <summary>
    /// Подбирает путь сайту, сработавший пишет в правила, ход и итог — в <paramref name="fix"/>.
    /// </summary>
    /// <returns>Имя сайта — для «Показать в «Маршрутах»»; <c>null</c> — подбор не состоялся.</returns>
    public static async Task<string?> RunAsync(string input, FixProgress fix, Button button, UserControl owner)
    {
        if (string.IsNullOrWhiteSpace(input) || !button.IsEnabled)
            return null;

        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "Проверяю…";
        fix.Start();

        var progress = new Progress<string>(fix.Now);
        var stepDone = new Progress<SiteFixStep>(fix.Add);

        try
        {
            var result = await Task.Run(() => SiteFixer.FixAsync(input, progress, CancellationToken.None, stepDone));

            Show(result, fix);

            Journal.Write("починка сайта", $"{result.Host}: "
                + string.Join("; ", result.Steps.Select(s => $"{s.Path} — {(s.Works ? "да" : "нет")}, {s.Detail}"))
                + result.Outcome());

            if (result.Applied is { } applied && result.NeedsRestart)
            {
                owner.Offer($"Маршрут для {result.Host}: " + (applied == RoutingMode.Proxy
                    ? "через VPN"
                    : result.Recipe is { } recipe ? $"десинк, {recipe}" : "десинк"));
            }

            return result.Host;
        }
        catch (Exception ex)
        {
            fix.Outcome(FixProgress.Glyphs.Warning, "Warn", "Проверка не удалась", ex.GetBaseException().Message);
            return null;
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = label;
        }
    }

    /// <summary>«Маршруты» на части, чьё правило теперь решает это имя (RoutesView.Reveal) — отбор сбрасывается на «Все».</summary>
    public static void ShowRoute(string? host, FrameworkElement from)
    {
        if (host is null)
            return;

        RoutesView.PendingReveal = host;
        (Window.GetWindow(from) as MainWindow)?.Open("routes");
    }

    /// <summary>Итог подбора — плашкой над ходом.</summary>
    private static void Show(SiteFixResult result, FixProgress fix)
    {
        fix.Finish(result.Steps);

        if (result.Host is null || result.Steps.Count == 0)
        {
            fix.Outcome(FixProgress.Glyphs.Warning, "Warn", result.Summary, null);
            return;
        }

        var rule = SiteFixer.RuleValue(result.Host);

        switch (result.Applied)
        {
            case RoutingMode.Direct:
                fix.Outcome(FixProgress.Glyphs.Ok, "Accent", $"Записан маршрут «напрямую» для {rule}",
                    "Десинк этому сайту мешал. Действует уже сейчас.", Reveal);
                break;

            case RoutingMode.Desync:
                fix.Outcome(FixProgress.Glyphs.Ok, "Accent",
                    $"Записан маршрут «десинк»{(result.Recipe is { } recipe ? $" с рецептом «{recipe}»" : string.Empty)} для {rule}",
                    "Заработает после перезапуска движков.", Reveal);
                break;

            case RoutingMode.Proxy:
                int recipes = result.Steps.Count(s => s.Try == RouteTry.Recipes && s.Recipe is not null);

                fix.Outcome(FixProgress.Glyphs.Ok, "Accent", $"Записан маршрут «через VPN» для {rule}",
                    "Заработает после перезапуска движков."
                    + (recipes > 0 ? $" Рецептов десинка опробовано {recipes} — ни один не открыл." : string.Empty),
                    Reveal);
                break;

            case null when result.Pinned is { } spare:
                fix.Outcome(FixProgress.Glyphs.Ok, "Accent", $"{result.Host} прибит к запасному адресу Cloudflare {spare}",
                    "На своём адресе сайт режут, на запасном открывается. Действует уже сейчас; снять — на вкладке «Файл hosts».");
                break;

            case null when result.Ours is { } section:
                fix.Outcome(FixProgress.Glyphs.Warning, "Warn", $"{result.Host} закрывает наш десинк — секция «{section}»",
                    result.Advice);
                break;

            default:
                bool open = result.Steps[0].Works;

                fix.Outcome(open ? FixProgress.Glyphs.Ok : FixProgress.Glyphs.No, open ? "Accent" : "Danger",
                    open ? $"{result.Host} открывается как есть — менять нечего" : $"{result.Host} не открылся ни одним путём — маршрут не менял",
                    result.Advice, Reveal);
                break;
        }
    }
}
