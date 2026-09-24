using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NetZapret.Core;
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
        // Размер — до показа: снимок под меню снимается раньше, чем меню
        // его закроет, а для снимка надо знать, где оно встанет.
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;

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
        Left = Math.Clamp(x - size.Width, left, Math.Max(left, right - size.Width));
        Top = Math.Clamp(y - size.Height, top, Math.Max(top, bottom - size.Height));

        // Вид — по настройкам «Меню в трее» в оформлении, читаются при каждом
        // открытии: сохранить там и есть применить здесь.
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var margin = Card.Margin;

        var backdrop = settings.TrayBlur
            ? Blurred(
                (int)Math.Round((Left + margin.Left) * dpi.DpiScaleX),
                (int)Math.Round((Top + margin.Top) * dpi.DpiScaleY),
                (int)Math.Round((size.Width - margin.Left - margin.Right) * dpi.DpiScaleX),
                (int)Math.Round((size.Height - margin.Top - margin.Bottom) * dpi.DpiScaleY))
            : null;

        // Размытие просили, а снимок не вышел — сплошная карточка, а не
        // прозрачная: прозрачности человек не выбирал.
        double density = settings.TrayBlur && backdrop is null
            ? 1
            : Math.Clamp(settings.TrayDensity, 0, 100) / 100.0;

        Card.Background = backdrop ?? (Brush)Brushes.Transparent;
        Plate.Background = Tint(density);

        Show();
        Activate();
    }

    /// <summary>
    /// Оттенок темы под текстом меню заданной плотности.
    /// </summary>
    /// <remarks>
    /// Цвет карточки берётся без его собственной прозрачности: у тем со стеклом
    /// она своя, и плотность тогда значила бы разное в разных темах.
    /// </remarks>
    private Brush Tint(double density)
    {
        var surface = TryFindResource("SurfaceColor") is Color c ? c : Colors.Black;

        return new SolidColorBrush(Color.FromRgb(surface.R, surface.G, surface.B)) { Opacity = density };
    }

    /// <summary>Радиус размытия в точках экрана — как у стекла под карточками окна.</summary>
    private const double BlurRadius = 28;

    /// <summary>
    /// Размытый снимок участка экрана — подложка карточки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Снимок, а не живое размытие. Системное размытие Windows — подложка DWM
    /// и политика акцента — под этим окном рисовало сплошной тёмный слой:
    /// снято 24.09 поверх ярких полос, в трёх вариантах окна. Меню живёт
    /// секунды и прячется при щелчке мимо, так что снимка на это время
    /// хватает, и на Windows 10 он работает так же.
    /// </para>
    /// <para>
    /// Снимается шире карточки на радиус и обрезается после размытия: края
    /// размытия иначе тянут к прозрачному и дают тёмную рамку — ровно так
    /// сделано и у стекла в окне (Themes.RenderLayer). У края экрана запас
    /// берётся, сколько есть: за краем снимать нечего.
    /// </para>
    /// </remarks>
    private static ImageBrush? Blurred(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        try
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            int pad = (int)Math.Ceiling(BlurRadius * 1.5);

            int left = Math.Max(screen.Left, x - pad);
            int top = Math.Max(screen.Top, y - pad);
            int right = Math.Min(screen.Right, x + width + pad);
            int bottom = Math.Min(screen.Bottom, y + height + pad);

            if (right - left < width || bottom - top < height)
                return null;

            using var shot = new System.Drawing.Bitmap(right - left, bottom - top);

            using (var graphics = System.Drawing.Graphics.FromImage(shot))
                graphics.CopyFromScreen(left, top, 0, 0, shot.Size);

            var handle = shot.GetHbitmap();
            BitmapSource source;

            try
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(handle);
            }

            var image = new Image
            {
                Source = source,
                Width = shot.Width,
                Height = shot.Height,
                Effect = new BlurEffect
                {
                    Radius = BlurRadius,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Quality,
                },
            };

            var whole = new Size(shot.Width, shot.Height);
            image.Measure(whole);
            image.Arrange(new Rect(whole));

            var rendered = new RenderTargetBitmap(shot.Width, shot.Height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(image);

            var cropped = new CroppedBitmap(rendered, new Int32Rect(x - left, y - top, width, height));
            cropped.Freeze();

            return new ImageBrush(cropped) { Stretch = Stretch.Fill };
        }
        catch (Exception)
        {
            // Снимок — украшение. Не вышел — карточка будет сплошной.
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

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
