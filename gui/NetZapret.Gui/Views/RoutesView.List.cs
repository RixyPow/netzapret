using System.Windows;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Gui.Views;

/// <summary>
/// «Добавить списком» (идея №14, владелец 19.10: «делай»): несколько сайтов
/// разом, один путь, по желанию — в папку.
/// </summary>
/// <remarks>
/// Два входа: вставка нескольких строк в поле поиска и пункт «Списком…»
/// в «Добавить правило». Поле однострочное, и вставленный список прежде
/// обрезался до первой строки — человек не видел, что остальное пропало.
/// </remarks>
public partial class RoutesView
{
    /// <summary>Вставка в поле поиска: список — в окно, одно имя — как прежде.</summary>
    private void OnSearchPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !RouteListInput.IsList(text))
            return;

        e.CancelCommand();
        AddList(text);
    }

    private void AddList(string text)
    {
        RouteListWindow window;

        try
        {
            window = new RouteListWindow(text, _engine, UserRulesFile.Load().Entries, _addInto, ProgramNames())
            {
                Owner = Window.GetWindow(this),
            };
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось прочитать маршруты: " + ex.GetBaseException().Message;
            return;
        }

        if (window.ShowDialog() != true || window.Plan is not { Count: > 0 } plan)
            return;

        _addInto = null;

        try
        {
            var file = UserRulesFile.Load();
            var written = RouteListInput.Write(plan, window.Mode, file);
            file.Save();

            if (window.Folder is { } folder)
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);
                var moved = settings.RouteFolders;

                foreach (var (match, value) in written)
                {
                    var key = match == MatchKind.Ip
                        ? RouteKeys.Make(RouteKeys.Subnet, value)
                        : RouteKeys.Make(RouteKeys.Own, value);

                    moved = RouteFolders.Move(moved, key, folder, null);
                }

                (settings with { RouteFolders = moved }).Save(AppSettings.DefaultPath);
                _open.Add(folder);
            }

            Search.Text = string.Empty;
            Reload();

            var path = window.Mode switch
            {
                RoutingMode.Proxy => "через VPN",
                RoutingMode.Desync => "десинк",
                _ => "напрямую",
            };

            Status.Text = $"Записано {plan.Count} → {path}"
                + (window.Folder is { } shown ? $", в папке «{shown}»" : string.Empty)
                + ". Путь отдельного сайта меняется в его строке. Применится при следующем запуске движков.";

            this.Offer($"Добавлено маршрутов: {plan.Count}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }
}
