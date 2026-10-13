using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Core.Services;

namespace NetZapret.Gui.Views;

/// <summary>
/// Папки «Маршрутов» по макету владельца 13.10: «Добавить папку», «⋯» у папки
/// и «+ Добавить маршрут в эту папку».
/// </summary>
/// <remarks>
/// Всё это — вид (<see cref="RouteFolders"/>): маршруты, их правила и путь
/// не меняются, перезапуск не нужен. Папка каталога («Discord») переименовывается
/// подписью (<see cref="AppSettings.FolderLabels"/>): её имя — ключ перекладки.
/// </remarks>
public partial class RoutesView
{
    /// <summary>Папка, в которую ляжет следующий добавленный свой маршрут; <c>null</c> — никуда.</summary>
    private string? _addInto;

    /// <summary>«Добавить папку»: пустая папка, куда перетаскивают маршруты.</summary>
    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var window = new FolderNameWindow("Новая папка", Taken()) { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { MadeFolders = [.. settings.MadeFolders, name] }).Save(AppSettings.DefaultPath);

            _open.Add(name);
            Search.Text = string.Empty;
            Reload();

            Status.Text = $"Папка «{name}» — наверху списка. Перетащите в неё маршруты или добавьте свой кнопкой в ней.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>«+ Добавить маршрут в эту папку»: поле поиска ждёт сайт, адрес или подсеть.</summary>
    private void OnAddIntoFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServiceRow folder })
            return;

        _addInto = folder.Name;
        Search.Text = string.Empty;
        Search.Focus();

        Status.Text = $"Впишите сайт, адрес или подсеть в поле выше — новый маршрут ляжет в папку «{folder.Shown}». "
            + "Существующий маршрут — перетащите на папку.";
    }

    /// <summary>
    /// Только что добавленный свой маршрут — в папку, из которой его добавляли.
    /// </summary>
    private void PlaceAdded(string key)
    {
        if (_addInto is not { } folder)
            return;

        _addInto = null;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { RouteFolders = RouteFolders.Move(settings.RouteFolders, key, folder, null) })
                .Save(AppSettings.DefaultPath);

            _open.Add(folder);
        }
        catch (Exception)
        {
            // Не легло в папку — маршрут всё равно записан, просто стоит сам по себе.
        }
    }

    /// <summary>«⋯» в шапке папки.</summary>
    private void OnFolderMore(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ServiceRow folder } anchor)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        if (folder.Program is { } program)
        {
            menu.Items.Add(MenuEntry("", "Убрать программу…", () => RemoveProgram(program)));
            menu.IsOpen = true;
            return;
        }

        menu.Items.Add(MenuEntry("", "Переименовать…", () => Rename(folder)));
        menu.Items.Add(MenuEntry("", "Добавить маршрут…", () => OnAddIntoFolder(anchor, e)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("", folder.Made ? "Удалить папку" : "Распустить папку", () => Dissolve(folder)));

        menu.IsOpen = true;
    }

    private void Rename(ServiceRow folder)
    {
        var window = new FolderNameWindow($"Переименовать «{folder.Shown}»", Taken(except: folder.Name), folder.Shown, "Переименовать")
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var labels = new Dictionary<string, string>(settings.FolderLabels, StringComparer.OrdinalIgnoreCase);

            if (string.Equals(name, folder.Name, StringComparison.Ordinal))
                labels.Remove(folder.Name);
            else
                labels[folder.Name] = name;

            (settings with { FolderLabels = labels }).Save(AppSettings.DefaultPath);
            Reload();

            Status.Text = $"Папка «{folder.Shown}» теперь «{name}».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Распустить папку: её маршруты — сами по себе; заведённая человеком — исчезает.
    /// </summary>
    /// <remarks>
    /// Без вопроса: маршруты не пропадают и путь не меняют, а вернуть каждый
    /// на место можно «⋯» в его строке.
    /// </remarks>
    private void Dissolve(ServiceRow folder)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var moved = settings.RouteFolders;

            foreach (var part in (_all.FirstOrDefault(s => s.Name == folder.Name && s.IsFolder) ?? folder).Parts)
                moved = RouteFolders.Move(moved, part.Key, string.Empty, part.HomeFolder);

            var labels = new Dictionary<string, string>(settings.FolderLabels, StringComparer.OrdinalIgnoreCase);
            labels.Remove(folder.Name);

            (settings with
            {
                RouteFolders = moved,
                MadeFolders = settings.MadeFolders.Where(f => !string.Equals(f, folder.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
                FolderLabels = labels,
                RouteOrder = settings.RouteOrder.Where(k => k != FolderPlace(folder.Name)).ToList(),
            }).Save(AppSettings.DefaultPath);

            _open.Remove(folder.Name);
            Reload();

            Status.Text = folder.Parts.Count == 0
                ? $"Папка «{folder.Shown}» удалена."
                : $"Папки «{folder.Shown}» больше нет, её маршруты — сами по себе. Путь у них прежний.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Имена, которые новой папке брать нельзя: программы и уже показанные папки.</summary>
    private IReadOnlyCollection<string> Taken(string? except = null) =>
        ProgramNames()
            .Concat(_all.Where(s => s.IsFolder && s.Name != except).SelectMany(s => new[] { s.Name, s.Shown }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
