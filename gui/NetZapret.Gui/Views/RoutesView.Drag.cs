using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Services;

namespace NetZapret.Gui.Views;

/// <summary>Куда ляжет перетаскиваемое.</summary>
internal enum DropKind
{
    /// <summary>Перед строкой.</summary>
    Before,

    /// <summary>За строкой.</summary>
    After,

    /// <summary>Внутрь: в папку или вместе с одиночным маршрутом — в новую.</summary>
    Into,
}

/// <summary>
/// Перетаскивание в «Маршрутах»: порядок папок и маршрутов, маршрут в папку
/// (владелец 13.10: «перетягивать папки и маршруты и засовывать маршруты
/// в папки именно удержанием»).
/// </summary>
/// <remarks>
/// <para>
/// Только вид, как и перекладка «⋯»: правила, их место в файле и путь
/// маршрута не меняются (<see cref="RouteFolders"/>). Порядок хранится
/// ключами (<see cref="AppSettings.RouteOrder"/>), папка — перекладкой
/// (<see cref="AppSettings.RouteFolders"/>).
/// </para>
/// <para>
/// Что куда — по месту над строкой. Над шапкой папки: верхняя четверть —
/// перед ней, нижняя — за ней, середина — внутрь (папку держим над
/// свёрнутой — она раскрывается). Над маршрутом в папке — перед ним или
/// за ним, в той же папке. Над одиночным маршрутом середина — новая папка
/// из двух. Папку тянут за шапку и кладут только между строками верхнего
/// уровня.
/// </para>
/// <para>
/// Порядок перетаскиванием виден только в раскладке «по умолчанию»: в
/// алфавите и «по методу» он ничего бы не значил, и там можно только
/// положить маршрут в папку.
/// </para>
/// </remarks>
public partial class RoutesView
{
    private const string DragFormat = "netzapret-route";

    /// <summary>Сколько держать над свёрнутой папкой, чтобы она раскрылась.</summary>
    private static readonly TimeSpan HoverOpen = TimeSpan.FromMilliseconds(700);

    /// <summary>Поле у края списка, где перетаскивание его прокручивает.</summary>
    private const double ScrollEdge = 48;

    private Point _pressAt;
    private object? _pressed;
    private object? _dragging;

    private DropMark? _mark;
    private FrameworkElement? _markOn;

    private DispatcherTimer? _hoverTimer;
    private ServiceRow? _hoverFolder;

    private ScrollViewer? _scroller;

    /// <summary>Нажатие на строку — может стать перетаскиванием.</summary>
    private void OnDragPress(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;

        if (sender is not FrameworkElement row || Interactive(e.OriginalSource as DependencyObject, row))
            return;

        _pressed = row.DataContext;
        _pressAt = e.GetPosition(this);
    }

    /// <summary>
    /// Нажали на список выбора, кнопку или выключатель внутри строки — это не перетаскивание.
    /// </summary>
    /// <remarks>
    /// Шапка папки сама кнопка, и её не считаем: за неё папку и тянут.
    /// Без этой проверки движение мыши в раскрытом списке выбора уводило бы
    /// строку из-под него.
    /// </remarks>
    private static bool Interactive(DependencyObject? source, FrameworkElement row)
    {
        for (var node = source; node is not null && node != row; node = UpTree(node))
        {
            if (node is ComboBox or ButtonBase or TextBoxBase or Hyperlink)
                return true;
        }

        return false;
    }

    private static DependencyObject? UpTree(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement row)
            return;

        var shift = e.GetPosition(this) - _pressAt;

        if (Math.Abs(shift.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(shift.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pressed;
        _pressed = null;

        // Маршруты программы живут в её папке; одиночную строку тянут за неё саму.
        if (item is PartRow { Movable: false } || item is ServiceRow { IsFolder: false })
            return;

        // Шапка папки — кнопка и держит мышь: без этого по отпускании она
        // сочла бы перетаскивание щелчком и свернулась.
        Mouse.Capture(null);

        _dragging = item;

        try
        {
            DragDrop.DoDragDrop(row, new DataObject(DragFormat, item), DragDropEffects.Move);
        }
        finally
        {
            _dragging = null;
            ClearMark();
            StopHover();
        }
    }

    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement row || _dragging is null || Decide(_dragging, row.DataContext, e.GetPosition(row).Y, row.ActualHeight) is not { } drop)
        {
            e.Effects = DragDropEffects.None;
            ClearMark();
            StopHover();
            return;
        }

        e.Effects = DragDropEffects.Move;
        ShowMark(row, drop);

        if (drop == DropKind.Into && row.DataContext is ServiceRow { IsFolder: true, Open: false } folder)
            StartHover(folder);
        else
            StopHover();
    }

    private void OnRowDragLeave(object sender, DragEventArgs e)
    {
        if (sender == _markOn)
            ClearMark();
    }

    private void OnRowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var item = _dragging;
        ClearMark();
        StopHover();

        if (sender is not FrameworkElement row || item is null
            || Decide(item, row.DataContext, e.GetPosition(row).Y, row.ActualHeight) is not { } drop)
        {
            return;
        }

        // Окно названия — следующим ходом: внутри Drop модальное окно
        // оставило бы перетаскивание висеть под ним.
        var target = row.DataContext;
        Dispatcher.BeginInvoke(() => Place(item, target, drop), DispatcherPriority.Background);
    }

    /// <summary>Прокрутка у края списка, пока тянут.</summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (_dragging is null)
            return;

        _scroller ??= Find<ScrollViewer>(Services);

        if (_scroller is null)
            return;

        double y = e.GetPosition(_scroller).Y;

        if (y < ScrollEdge)
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 14);
        else if (y > _scroller.ActualHeight - ScrollEdge)
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 14);
    }

    /// <summary>Можно ли сюда и как; <c>null</c> — нельзя.</summary>
    private DropKind? Decide(object dragging, object? target, double y, double height)
    {
        if (height <= 0 || ReferenceEquals(dragging, target))
            return null;

        double at = y / height;
        bool ordered = _order == RouteOrderBy.Catalog;

        DropKind? Edge(double edge) => !ordered ? null : at < edge ? DropKind.Before : at > 1 - edge ? DropKind.After : null;

        switch (dragging, target)
        {
            // Маршрут над шапкой папки.
            case (PartRow route, ServiceRow { IsFolder: true } folder):
                if (folder.Program is not null || string.Equals(route.Folder, folder.Name, StringComparison.OrdinalIgnoreCase) && !ordered)
                    return Edge(0.5);

                return Edge(0.25) ?? DropKind.Into;

            // Маршрут над маршрутом.
            case (PartRow route, PartRow other):
                if (route.Key == other.Key || !other.Movable)
                    return null;

                if (other.Folder is not null)
                    return ordered ? (at < 0.5 ? DropKind.Before : DropKind.After) : DropKind.Into;

                return Edge(0.25) ?? DropKind.Into;

            // Папка — только между строками верхнего уровня.
            case (ServiceRow folder, ServiceRow { IsFolder: true } other):
                return folder.Name == other.Name ? null : Edge(0.5);

            case (ServiceRow, PartRow { Folder: null }):
                return Edge(0.5);

            default:
                return null;
        }
    }

    /// <summary>Записывает перекладку и порядок — и перечитывает раздел.</summary>
    private void Place(object item, object? target, DropKind drop)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var folders = settings.RouteFolders;
            var order = (IReadOnlyList<string>)_keyOrder;
            string done;

            switch (item, target)
            {
                case (PartRow route, ServiceRow folder) when drop == DropKind.Into:
                    folders = RouteFolders.Move(folders, route.Key, folder.Name, route.HomeFolder);
                    order = RouteFolders.MoveKeys(order, [route.Key], LastKey(folder), after: true);
                    _open.Add(folder.Name);
                    done = $"«{route.Title}» — в папке «{folder.Name}».";
                    break;

                case (PartRow route, ServiceRow folder):
                    folders = RouteFolders.Move(folders, route.Key, string.Empty, route.HomeFolder);
                    order = RouteFolders.MoveKeys(order, [route.Key],
                        drop == DropKind.Before ? FirstKey(folder) : LastKey(folder), drop == DropKind.After);
                    done = $"«{route.Title}» — вне папки.";
                    break;

                case (PartRow route, PartRow other) when drop == DropKind.Into && other.Folder is null:
                    var window = new FolderNameWindow(route.Title, ProgramNames()) { Owner = Window.GetWindow(this) };

                    if (window.ShowDialog() != true || window.Chosen is not { } name)
                        return;

                    folders = RouteFolders.Move(folders, other.Key, name, other.HomeFolder);
                    folders = RouteFolders.Move(folders, route.Key, name, route.HomeFolder);
                    order = RouteFolders.MoveKeys(order, [route.Key], other.Key, after: true);
                    _open.Add(name);
                    done = $"«{other.Title}» и «{route.Title}» — в новой папке «{name}».";
                    break;

                case (PartRow route, PartRow other):
                    var into = other.Folder;
                    folders = RouteFolders.Move(folders, route.Key, into ?? string.Empty, route.HomeFolder);
                    order = RouteFolders.MoveKeys(order, [route.Key], other.Key, drop == DropKind.After);
                    done = into is null ? $"«{route.Title}» переставлен." : $"«{route.Title}» — в папке «{into}».";
                    break;

                case (ServiceRow folder, ServiceRow other):
                    order = RouteFolders.MoveKeys(order, Keys(folder),
                        drop == DropKind.Before ? FirstKey(other) : LastKey(other), drop == DropKind.After);
                    done = $"Папка «{folder.Name}» переставлена.";
                    break;

                case (ServiceRow folder, PartRow other):
                    order = RouteFolders.MoveKeys(order, Keys(folder), other.Key, drop == DropKind.After);
                    done = $"Папка «{folder.Name}» переставлена.";
                    break;

                default:
                    return;
            }

            (settings with { RouteFolders = folders, RouteOrder = order.ToList() }).Save(AppSettings.DefaultPath);
            Reload();

            Status.Text = done + " Путь маршрутов не менялся.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переложить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Все маршруты папки — из полного списка, а не из отобранного поиском.</summary>
    private IReadOnlyList<string> Keys(ServiceRow folder) =>
        (_all.FirstOrDefault(s => s.Name == folder.Name && s.IsFolder) ?? folder).Parts.Select(p => p.Key).ToList();

    private string FirstKey(ServiceRow folder) => OrderOf(Keys(folder)).First();

    private string LastKey(ServiceRow folder) => OrderOf(Keys(folder)).Last();

    private IEnumerable<string> OrderOf(IReadOnlyList<string> keys) =>
        keys.OrderBy(k => _keyOrder.IndexOf(k) is var at and >= 0 ? at : int.MaxValue);

    private IReadOnlyCollection<string> ProgramNames() =>
        AppSettings.Load(AppSettings.DefaultPath).ProgramRoutes.Select(p => p.Program).ToList();

    private void ShowMark(FrameworkElement row, DropKind drop)
    {
        if (_markOn == row && _mark?.Kind == drop)
            return;

        ClearMark();

        if (AdornerLayer.GetAdornerLayer(row) is not { } layer)
            return;

        _mark = new DropMark(row, drop, (Brush)FindResource("Accent"));
        _markOn = row;
        layer.Add(_mark);
    }

    private void ClearMark()
    {
        if (_mark is not null && _markOn is not null)
            AdornerLayer.GetAdornerLayer(_markOn)?.Remove(_mark);

        _mark = null;
        _markOn = null;
    }

    /// <summary>Держат маршрут над свёрнутой папкой — раскрыть её, чтобы положить в нужное место.</summary>
    private void StartHover(ServiceRow folder)
    {
        if (_hoverFolder?.Name == folder.Name)
            return;

        StopHover();
        _hoverFolder = folder;
        _hoverTimer = new DispatcherTimer(HoverOpen, DispatcherPriority.Background, (_, _) =>
        {
            StopHover();
            folder.Open = true;
            Remember(folder.Name, true);
            Redraw();
        }, Dispatcher);
    }

    private void StopHover()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        _hoverFolder = null;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T found)
                return found;

            if (Find<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }
}

/// <summary>Метка места: черта перед строкой, за ней или рамка вокруг.</summary>
internal sealed class DropMark : Adorner
{
    private readonly Pen _pen;

    public DropKind Kind { get; }

    public DropMark(UIElement element, DropKind kind, Brush brush) : base(element)
    {
        Kind = kind;
        IsHitTestVisible = false;
        _pen = new Pen(brush, kind == DropKind.Into ? 2 : 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pen.Freeze();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var size = AdornedElement.RenderSize;

        switch (Kind)
        {
            case DropKind.Into:
                drawing.DrawRoundedRectangle(null, _pen, new Rect(1, 1, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2)), 14, 14);
                break;

            case DropKind.Before:
                drawing.DrawLine(_pen, new Point(8, -3), new Point(size.Width - 8, -3));
                break;

            default:
                drawing.DrawLine(_pen, new Point(8, size.Height - 3), new Point(size.Width - 8, size.Height - 3));
                break;
        }
    }
}
