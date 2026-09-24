using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Media;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>Что показать в меню трея — снимок состояния движков.</summary>
internal sealed record TrayStatus(
    bool Running,
    bool AllHealthy,
    ServiceHealth? Tunnel,
    ServiceHealth? Desync,
    bool Busy)
{
    public string Headline => !Running
        ? "Остановлено"
        : AllHealthy ? "Работает" : "Работает с оговорками";

    /// <summary>Из файла состояния надзора — того же, что читает «Главная».</summary>
    public static TrayStatus Read(bool busy)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        ServiceHealth? Of(string name) => running
            ? state!.Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Health
            : null;

        return new TrayStatus(
            running,
            running && state!.Services.All(s => s.Health == ServiceHealth.Healthy),
            Of("sing-box"),
            Of("winws2"),
            busy);
    }

    public static string Describe(ServiceHealth? health) => health switch
    {
        ServiceHealth.Healthy => "работает",
        ServiceHealth.Degraded => "с оговорками",
        ServiceHealth.Dead => "упал",
        ServiceHealth.Stopped => "остановлен",

        // Службы нет в состоянии — её и не поднимали: выключена выключателем
        // на «Главной». «Остановлен» здесь соврал бы, что она была.
        _ => "выключен",
    };
}

/// <summary>
/// Меню значка в трее — карточка в стиле окна.
/// </summary>
/// <remarks>
/// <para>
/// Окно создаётся один раз и прячется, а не закрывается: открывают его часто,
/// а тема, шрифт и масштаб у него и так живые — всё на DynamicResource.
/// </para>
/// <para>
/// Прячется, когда теряет фокус, — как любое меню: щелчок мимо закрывает его.
/// </para>
/// </remarks>
public partial class TrayMenu : Window
{
    private readonly Action _toggle;
    private readonly Action _showWindow;
    private readonly Action<bool> _quit;

    internal TrayMenu(Action toggle, Action showWindow, Action<bool> quit)
    {
        _toggle = toggle;
        _showWindow = showWindow;
        _quit = quit;

        InitializeComponent();

        Version.Text = MainWindow.Version();
    }

    internal void Render(TrayStatus status)
    {
        State.Text = status.Headline;
        Dot.SetResourceReference(
            Shape.FillProperty,
            !status.Running ? "Faint" : status.AllHealthy ? "Accent" : "Warn");

        Tunnel.Text = status.Running ? TrayStatus.Describe(status.Tunnel) : "—";
        Desync.Text = status.Running ? TrayStatus.Describe(status.Desync) : "—";

        Toggle.Content = status.Busy
            ? (status.Running ? "Останавливаю…" : "Запускаю…")
            : status.Running ? "Остановить" : "Запустить";
        Toggle.IsEnabled = !status.Busy;
    }

    /// <summary>Показывает меню у указателя, в пределах рабочей области его экрана.</summary>
    internal void PopUp()
    {
        // Сперва за краем экрана: размер известен только после показа,
        // а показанное в углу по умолчанию мигнуло бы там на глазах.
        Left = -10000;
        Top = -10000;

        Show();
        UpdateLayout();

        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);

        // Точки экрана — в единицы WPF: на 150 % масштаба Windows это разное.
        double x = cursor.X / dpi.DpiScaleX;
        double y = cursor.Y / dpi.DpiScaleY;
        double left = area.Left / dpi.DpiScaleX;
        double top = area.Top / dpi.DpiScaleY;
        double right = area.Right / dpi.DpiScaleX;
        double bottom = area.Bottom / dpi.DpiScaleY;

        // Правым нижним углом к указателю — трей обычно внизу справа, — но
        // не за пределы рабочей области: панель задач бывает и слева, и сверху.
        Left = Math.Clamp(x - ActualWidth, left, Math.Max(left, right - ActualWidth));
        Top = Math.Clamp(y - ActualHeight, top, Math.Max(top, bottom - ActualHeight));

        Activate();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Hide();
    }

    private void OnToggle(object sender, RoutedEventArgs e) => _toggle();

    private void OnShowWindow(object sender, RoutedEventArgs e)
    {
        Hide();
        _showWindow();
    }

    private void OnQuitKeep(object sender, RoutedEventArgs e)
    {
        Hide();
        _quit(false);
    }

    private void OnQuitStop(object sender, RoutedEventArgs e)
    {
        // Не прячется: пока гаснут движки, человек видит «Останавливаю…».
        _quit(true);
    }
}
