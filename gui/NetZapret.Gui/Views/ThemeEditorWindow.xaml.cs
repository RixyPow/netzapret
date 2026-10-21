using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NetZapret.Core;
using NetZapret.Core.Themes;

namespace NetZapret.Gui.Views;

/// <summary>Строка цвета в редакторе темы.</summary>
public sealed record ColorRow(string Slot, string Label, string Note, ThemeColor Color, string? Warning)
{
    public Brush Swatch => new SolidColorBrush(System.Windows.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));

    public string Hex => Color.ToString();

    public Visibility WarningShown => Warning is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Редактор темы: цвета, фон, шрифт — видно сразу, записывается в themes\.
/// </summary>
/// <remarks>
/// <para>
/// Начинается с цветов темы, выбранной сейчас: так проще всего получить
/// «такую же, только с фиолетовой кнопкой». Правимую встроенную тему
/// ThemeWriter не перезаписывает — она уходит новой.
/// </para>
/// <para>
/// Владелец, 24.09: сменил «Фон окна» на красный, сохранил — и тема «вообще
/// сдохла»: редактор молча перезаписал рабочую Nirvana нечитаемой,
/// и программа откатилась на встроенную. Отсюда три правила. Каждая правка
/// видна на окне сразу, пока тема читается. Нечитаемый цвет отмечен в своей
/// строке, и «Поправить нечитаемое» подстраивает его светлоту. Рабочая тема
/// нечитаемой не перезаписывается — такая сохраняется копией.
/// </para>
/// </remarks>
public partial class ThemeEditorWindow : Window
{
    /// <summary>Подписи слотов словами — как их видит человек, а не как они зовутся в файле.</summary>
    private static readonly (string Slot, string Label, string Note)[] Slots =
    [
        (ThemeSlots.Backdrop, "Фон окна", "под всем; им же затемняется картинка"),
        (ThemeSlots.Surface, "Карточки", "строки, группы, меню"),
        (ThemeSlots.Raised, "Кнопки и поля", "то, что нажимают и во что пишут"),
        (ThemeSlots.Border, "Рамки", "обводки и разделители"),
        (ThemeSlots.Text, "Текст", "основной"),
        (ThemeSlots.Muted, "Пояснения и меню", "второй план"),
        (ThemeSlots.Faint, "Третий план", "необязательные подписи"),
        (ThemeSlots.AccentFill, "Главная кнопка", "заливка; надпись на ней подберётся сама"),
        (ThemeSlots.OnAccent, "Надпись на главной кнопке", "чёрная или белая — что читается"),
        (ThemeSlots.Accent, "«Работает»", "зелёный по смыслу"),
        (ThemeSlots.Danger, "«Закрыто»", "красный по смыслу"),
        (ThemeSlots.Warn, "«Внимание»", "жёлтый по смыслу"),
        (ThemeSlots.WarnFill, "Точки «внимание»", "мелкие метки"),
        (ThemeSlots.AccentDim, "Выделение в поле", "фон выделенного текста"),
    ];

    /// <summary>
    /// Цвета значка (владелец 21.10: «3 цвета для иконки») — необязательные:
    /// пока не выбраны, значок следует за главной кнопкой, фоном окна и текстом
    /// (<see cref="Theme.Logo"/>) и в тему не пишется.
    /// </summary>
    private static readonly (string Slot, string Label, string Note)[] LogoSlots =
    [
        (ThemeSlots.LogoTop, "Значок: верх", "над кривой; не выбран — как главная кнопка"),
        (ThemeSlots.LogoBand, "Значок: кривая", "не выбрана — как фон окна, прорезью"),
        (ThemeSlots.LogoBottom, "Значок: низ", "под кривой; не выбран — как текст"),
    ];

    private static readonly double[] Dims = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];
    private static readonly double[] Blurs = [0, 12, 24, 40];
    private static readonly double[] Alphas = [1, 0.9, 0.8, 0.7, 0.6];

    private const string BaseFont = "как в основе";

    private readonly Theme _from;
    private readonly Dictionary<string, ThemeColor> _colors;
    private readonly string? _editing;
    private readonly string _original;
    private readonly bool _editingWasFine;
    private string? _image;
    private bool _ready;

    // Арт на «Главной»: точка центра и увеличение (SideArtLayout, 15.10).
    // Картинка на «Главной», уложенная руками (HomePlacement, 15.10); _placed — уложена.
    private double _homeX = 0.5, _homeY = 0.5, _homeSize = 1;
    private bool _placed;
    private bool _dragging;

    /// <summary>
    /// Выбор темы для «Главной» (<see cref="Theme.HomeFullWidth"/>); <c>null</c> — как
    /// решит картинка. Совпавший с решением картинки выбор не пишется: снимешь
    /// картинку — тема без неё и так на всю ширину.
    /// </summary>
    private bool? _homeWide;

    private bool HomeWide => _homeWide ?? _image is null;

    /// <summary>Картинка для миниатюры «Главной»; у видео — его кадр.</summary>
    private BitmapSource? _poster;

    /// <summary>
    /// Применить на окне — не на каждое движение мыши: сборка темы считает
    /// стекло (размытие всей картинки), и протяжка точки им захлебнулась бы.
    /// </summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _saved;

    /// <summary>Тема, которую записали и применили; <c>null</c> — отменили.</summary>
    public string? SavedId { get; private set; }

    /// <summary>Сохранена копией, потому что правка не читалась.</summary>
    public bool SavedAsCopy { get; private set; }

    /// <param name="from">Тема, с которой начинаем.</param>
    /// <param name="editing">Имя папки правимой темы; <c>null</c> — новая.</param>
    public ThemeEditorWindow(Theme from, string? editing)
    {
        InitializeComponent();

        _from = from;
        _homeWide = from.HomeFullWidth;
        _editing = editing;
        _original = Themes.Current;
        _editingWasFine = editing is not null && ThemeLoader.Load(editing).Ok;
        _colors = ThemeSlots.All.ToDictionary(s => s, s => from[s] with { A = s == ThemeSlots.Surface ? from[s].A : (byte)255 });

        // Цвета значка — только заданные темой: незаданный следует за её цветами.
        foreach (var slot in ThemeSlots.Logo)
        {
            if (from.Colors.TryGetValue(slot, out var logo))
                _colors[slot] = logo with { A = 255 };
        }
        _image = from.Background?.Image;
        if (from.Background is { HomePlaced: true } placed)
        {
            (_homeX, _homeY, _homeSize) = (placed.HomeX!.Value, placed.HomeY!.Value, placed.HomeSize!.Value);
            _placed = true;
        }

        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Changed();
        };

        Heading.Text = editing is null ? "Своя тема" : $"Правка темы «{from.Name}»";
        Title = Heading.Text;
        NameField.Text = editing is null ? from.Name + " — моя" : from.Name;

        DimChoice.ItemsSource = Dims.Select(d => $"{d * 100:0} %").ToList();
        AlphaChoice.ItemsSource = Alphas.Select(a => a >= 1 ? "непрозрачные" : $"{a * 100:0} %").ToList();

        var background = from.Background;
        FitChoice.SelectedIndex = (int)(background?.Fit ?? BackgroundFit.Cover);
        DimChoice.SelectedIndex = Nearest(Dims, background?.Dim ?? 0.6);
        BlurChoice.SelectedIndex = Nearest(Blurs, background?.Blur ?? 24);
        AlphaChoice.SelectedIndex = Nearest(Alphas, from[ThemeSlots.Surface].A / 255.0);

        var fonts = new List<AppearanceView.FontItem> { new(BaseFont, new FontFamily(from.Fonts.Ui)) };
        fonts.AddRange(Fonts.SystemFontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f.Source))
            .GroupBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppearanceView.FontItem(g.Key, g.First()))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase));

        FontChoice.ItemsSource = fonts;
        FontChoice.SelectedItem = fonts.FirstOrDefault(f => f.Name == from.Fonts.Display && f.Name != BaseFont) ?? fonts[0];

        ShowImage();

        _ready = true;
        Changed();

        Closing += OnClosing;

        // Владелец окна (а с ним размер страницы «Главной») известен только после
        // конструктора — миниатюра раскладывается заново, когда окно встало.
        Loaded += (_, _) => ShowMini();
    }

    private static int Nearest(double[] steps, double value) =>
        steps.Select((s, i) => (Distance: Math.Abs(s - value), Index: i)).MinBy(x => x.Distance).Index;

    private string? ChosenFont => FontChoice.SelectedItem is AppearanceView.FontItem { Name: var name } && name != BaseFont ? name : null;

    /// <summary>Цвета с плотностью карточек из выбора — как они запишутся.</summary>
    private Dictionary<string, ThemeColor> Draft()
    {
        var colors = new Dictionary<string, ThemeColor>(_colors);

        // Плотность — только у карточек и только с картинкой: у сплошного
        // фона прозрачная карточка просто темнее, и смысла в этом нет.
        colors[ThemeSlots.Surface] = _image is not null && AlphaChoice.SelectedIndex >= 0
            ? colors[ThemeSlots.Surface] with { A = (byte)Math.Round(Alphas[AlphaChoice.SelectedIndex] * 255) }
            : colors[ThemeSlots.Surface] with { A = 255 };

        return colors;
    }

    /// <summary>Тема в памяти — такая, какой она запишется.</summary>
    private Theme Preview() => new()
    {
        Id = _editing ?? "preview",
        Name = NameField.Text.Trim(),
        Folder = _from.Folder,
        Colors = Draft(),
        Fonts = ChosenFont is { } font
            ? new ThemeFonts { Ui = font, Display = font, Mono = _from.Fonts.Mono }
            : _from.Fonts,
        Background = _image is null
            ? null
            : new ThemeBackground
            {
                Image = _image,
                Fit = (BackgroundFit)Math.Max(0, FitChoice.SelectedIndex),
                Dim = Dims[Math.Max(0, DimChoice.SelectedIndex)],
                Blur = Blurs[Math.Max(0, BlurChoice.SelectedIndex)],
                HomeX = _placed ? _homeX : null,
                HomeY = _placed ? _homeY : null,
                HomeSize = _placed ? _homeSize : null,
            },
        HomeFullWidth = _homeWide,
    };

    /// <summary>
    /// Любая правка: отметить нечитаемое в строках и показать тему на окне.
    /// </summary>
    private void Changed()
    {
        if (!_ready)
            return;

        var draft = Draft();
        var failing = ThemeFixer.Failing(draft);

        var preview = Preview();

        ColorRows.ItemsSource = Slots
            .Select(s => new ColorRow(s.Slot, s.Label, s.Note, draft[s.Slot], failing.GetValueOrDefault(s.Slot)))
            .Concat(LogoSlots.Select(s => new ColorRow(s.Slot, s.Label, s.Note, preview.Logo(s.Slot), null)))
            .ToList();

        FixButton.Visibility = failing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var problems = Themes.Preview(Preview());

        Say(problems.Count == 0
            ? string.Empty
            : "На окне — последняя читаемая версия. Не читается: "
              + string.Join("; ", problems.Take(2)).Replace("не читается: ", string.Empty)
              + (problems.Count > 2 ? $" и ещё {problems.Count - 2}." : "."));

        // Миниатюра «Главной» — теми же цветами и затемнением.
        ShowMini();

        // Картинка тоже может не читаться — тогда поправить цвета мало,
        // и «Поправить» стоит показать всё равно: светлее текст — читаемее.
        if (problems.Count > 0)
            FixButton.Visibility = Visibility.Visible;
    }

    private void OnChanged(object sender, SelectionChangedEventArgs e) => Changed();

    private void ShowImage()
    {
        ImageName.Text = _image is null ? "нет — фон сплошной, цветом «Фон окна»" : Path.GetFileName(_image);
        ClearImage.IsEnabled = _image is not null;
        ImageSettings.Visibility = _image is null ? Visibility.Collapsed : Visibility.Visible;

        _poster = _image is null ? null : Poster(_image);
        ShowMini();
        ShowHomeWide();
    }

    private void OnHomeWide(object sender, RoutedEventArgs e)
    {
        bool wide = !HomeWide;
        _homeWide = wide == (_image is null) ? null : wide;

        ShowHomeWide();
        Changed();
    }

    /// <summary>Выключатель «на всю ширину» и миниатюра: во всю ширину укладка не нужна — миниатюра бледнеет.</summary>
    private void ShowHomeWide()
    {
        bool wide = HomeWide;

        HomeWideSwitch.IsChecked = wide;
        HomeMini.Opacity = wide ? 0.45 : 1;
        HomeMini.IsHitTestVisible = !wide;
        SizeSlider.IsEnabled = !wide;
    }

    /// <summary>Картинка для миниатюры: сама картинка в малом размере, у видео — его кадр.</summary>
    private static BitmapSource? Poster(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                return VideoPoster.Load(path, size: 480);

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 640;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // Нечитаемая картинка — миниатюра без неё; сама тема скажет о ней проверкой.
            return null;
        }
    }

    /// <summary>
    /// Страница «Главной», на которую ляжет картинка, — место раздела в окне
    /// программы; окна нет (редактор открыт не из него) — развёрнутое окно владельца.
    /// </summary>
    /// <remarks>
    /// Владелец 21.10, снимки миниатюры и «Главной»: «миниатюра врёт». Миниатюра
    /// держала свою страницу — 480×307 с колонкой картинки «300 из 1240», а у
    /// развёрнутого окна страница 1302×794 и колонка 362: карточка выходила уже
    /// и правее, и картинка, уложенная долями страницы, попадала в неё другим
    /// куском. Теперь страница миниатюры — тех же пропорций, что настоящая,
    /// и карточка на ней — той же формулой (SideArtLayout.Tile).
    /// </remarks>
    private Size Page => Owner is MainWindow { PageSize: { Width: > 0, Height: > 0 } size } ? size : new Size(1302, 794);

    private double MiniScale => HomeMini.Width / Page.Width;

    /// <summary>Миниатюра — тех же пропорций, что страница: ширина своя, высота по ней.</summary>
    private void SizeMini()
    {
        double height = Math.Round(HomeMini.Width * Page.Height / Page.Width);

        if (Math.Abs(HomeMini.Height - height) > 0.5)
            HomeMini.Height = height;
    }

    /// <summary>Карточка с картинкой на миниатюре; пусто — на такой странице её нет.</summary>
    private Rect MiniTile() => SideArtLayout.Tile(Page.Width, Page.Height) is { } tile
        ? new Rect(tile.Left * MiniScale, tile.Top * MiniScale, tile.Width * MiniScale, tile.Height * MiniScale)
        : Rect.Empty;

    /// <summary>
    /// Где карточки «Главной»: по ширине — доли колонки карточек (без её полей
    /// 32), по высоте — доли страницы.
    /// </summary>
    private static readonly (double X, double Y, double W, double H)[] MiniCardsAt =
    [
        (0, 0.10, 1, 0.16),
        (0, 0.30, 0.493, 0.20), (0.507, 0.30, 0.493, 0.20),
        (0, 0.55, 1, 0.17),
        (0, 0.76, 0.493, 0.15), (0.507, 0.76, 0.493, 0.15),
    ];

    private double MiniAspect => _poster is { PixelWidth: > 0, PixelHeight: > 0 } p ? (double)p.PixelWidth / p.PixelHeight : 1;

    /// <summary>Место картинки на миниатюре — тем же расчётом, что у «Главной».</summary>
    private Rect MiniPlace()
    {
        double width = HomeMini.Width, height = HomeMini.Height;
        var tile = MiniTile();
        double centre = tile.IsEmpty ? width / 2 : tile.Left + tile.Width / 2;

        var (left, top, w, h) = _placed
            ? SideArtLayout.Place(width, height, MiniAspect, _homeX, _homeY, _homeSize)
            : SideArtLayout.Fit(width, height, centre, MiniAspect);

        return new Rect(left, top, w, h);
    }

    /// <summary>Миниатюра «Главной»: картинка в карточке справа (вне её — тенью), карточки и рамка.</summary>
    private void ShowMini()
    {
        SizeMini();
        var place = MiniPlace();
        var (_, _, size) = SideArtLayout.Measure(HomeMini.Width, HomeMini.Height, (place.Left, place.Top, place.Width, place.Height));

        var was = _ready;
        _ready = false;
        SizeSlider.Value = Math.Clamp(size, SizeSlider.Minimum, SizeSlider.Maximum);
        _ready = was;
        SizeText.Text = _placed ? $"{size * 100:0} %" : "как было";

        if (_poster is null)
        {
            MiniArt.Fill = MiniGround.Fill = MiniShade.Fill = MiniGhost.Fill = null;
            MiniCards.Children.Clear();
            return;
        }

        var backdrop = _colors[ThemeSlots.Backdrop];
        var tile = MiniTile();

        // Как на «Главной»: картинка лежит по странице, видна в карточке справа;
        // на узком окне карточки нет — и картинки на «Главной» нет.
        MiniArtLayer.Clip = tile.IsEmpty ? Geometry.Empty : new RectangleGeometry(tile, 5, 5);

        MiniGround.Fill = _placed ? HomePlacement.Ground(_poster, backdrop) : null;
        MiniArt.Fill = new ImageBrush(_poster)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = place,
        };
        MiniArt.OpacityMask = _placed ? HomePlacement.Feather(place) : null;
        MiniGhost.Fill = MiniArt.Fill;
        MiniGhost.OpacityMask = MiniArt.OpacityMask;

        // Затемнение, как у «Главной» над персонажем: лёгкое и ровное (ArtDim, 0,12).
        MiniShade.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, backdrop.R, backdrop.G, backdrop.B));

        var surface = Draft()[ThemeSlots.Surface];
        var line = _colors[ThemeSlots.Border];
        var card = new SolidColorBrush(System.Windows.Media.Color.FromArgb(surface.A, surface.R, surface.G, surface.B));
        var edge = new SolidColorBrush(System.Windows.Media.Color.FromRgb(line.R, line.G, line.B));

        MiniCards.Children.Clear();

        // Колонка карточек — от поля 32 до поля 32 своей ширины, как на «Главной».
        double column = (SideArtLayout.Content(Page.Width) - 64) * MiniScale;
        double margin = 32 * MiniScale;

        foreach (var (x, y, w, h) in MiniCardsAt)
        {
            var shape = new System.Windows.Shapes.Rectangle
            {
                Width = w * column,
                Height = h * HomeMini.Height,
                RadiusX = 5,
                RadiusY = 5,
                Fill = card,
                Stroke = edge,
                StrokeThickness = 1,
            };

            Canvas.SetLeft(shape, margin + x * column);
            Canvas.SetTop(shape, y * HomeMini.Height);
            MiniCards.Children.Add(shape);
        }

        if (tile.IsEmpty)
            return;

        var frame = new System.Windows.Shapes.Rectangle
        {
            Width = tile.Width,
            Height = tile.Height,
            RadiusX = 5,
            RadiusY = 5,
            Stroke = edge,
            StrokeThickness = 1,
        };

        Canvas.SetLeft(frame, tile.Left);
        Canvas.SetTop(frame, tile.Top);
        MiniCards.Children.Add(frame);
    }

    private Point _dragFrom;
    private (double X, double Y) _dragStart;

    private void OnMiniDown(object sender, MouseButtonEventArgs e)
    {
        if (_poster is null)
            return;

        // Первая протяжка начинает с того, как картинка лежала, — без прыжка.
        Adopt();
        _dragging = true;
        _dragFrom = e.GetPosition(HomeMini);
        _dragStart = (_homeX, _homeY);
        HomeMini.CaptureMouse();
    }

    private void OnMiniMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var shift = e.GetPosition(HomeMini) - _dragFrom;
        _homeX = Math.Clamp(_dragStart.X + shift.X / HomeMini.Width, 0, 1);
        _homeY = Math.Clamp(_dragStart.Y + shift.Y / HomeMini.Height, 0, 1);
        ShowMini();
        Settle();
    }

    private void OnMiniUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        HomeMini.ReleaseMouseCapture();
    }

    /// <summary>Колесо с Ctrl — размер картинки; без Ctrl — прокрутка страницы редактора.</summary>
    /// <remarks>
    /// Владелец 21.10: «сделай так, чтобы прокрутка страницы не трогала размер
    /// фона». Колесо без Ctrl меняло размер, стоило миниатюре проехать под
    /// мышью, пока страницу прокручивают, — и укладка сбивалась незаметно.
    /// </remarks>
    private void OnMiniWheel(object sender, MouseWheelEventArgs e)
    {
        if (_poster is null || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        Adopt();
        _homeSize = Math.Clamp(_homeSize * Math.Pow(1.1, e.Delta / 120.0), SideArtLayout.MinSize, SideArtLayout.MaxSize);
        ShowMini();
        Settle();
    }

    private void OnSize(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _poster is null)
            return;

        Adopt();
        _homeSize = e.NewValue;
        ShowMini();
        Settle();
    }

    /// <summary>Перейти к уложенной руками — с того места, где картинка лежит сейчас.</summary>
    private void Adopt()
    {
        if (_placed)
            return;

        var place = MiniPlace();
        (_homeX, _homeY, _homeSize) = SideArtLayout.Measure(HomeMini.Width, HomeMini.Height, (place.Left, place.Top, place.Width, place.Height));
        _homeX = Math.Clamp(_homeX, 0, 1);
        _homeY = Math.Clamp(_homeY, 0, 1);
        _homeSize = Math.Clamp(_homeSize, SideArtLayout.MinSize, SideArtLayout.MaxSize);
        _placed = true;
    }

    private void OnHomeReset(object sender, RoutedEventArgs e)
    {
        _placed = false;
        ShowMini();
        Changed();
    }

    /// <summary>Применить на окне, когда протяжка или ползунок затихнут.</summary>
    private void Settle()
    {
        _settle.Stop();
        _settle.Start();
    }

    private void OnColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot })
            return;

        var label = Slots.Concat(LogoSlots).First(s => s.Slot == slot).Label;
        var current = ThemeSlots.Logo.Contains(slot) ? Preview().Logo(slot) : _colors[slot];
        var picker = new ColorPickerWindow(current, label) { Owner = this };

        if (picker.ShowDialog() != true)
            return;

        // У значка — выбранный становится заданным: дальше он не следует за цветами темы.
        _colors[slot] = picker.Chosen with { A = current.A };

        // Подпись на главной кнопке — следом за заливкой: иначе первая же
        // светлая заливка с белой подписью не прошла бы проверку.
        if (slot == ThemeSlots.AccentFill)
            _colors[ThemeSlots.OnAccent] = ColorMath.ReadableOn(_colors[slot]);

        Changed();
    }

    /// <summary>
    /// Подстраивает светлоту нечитаемых цветов, не трогая выбранное.
    /// </summary>
    /// <remarks>
    /// Если не читается текст на картинке, а не на цветах, — поднимает
    /// затемнение по ступени, пока не прочтётся: выбранную картинку
    /// менять нельзя, а затемнение и есть то, чем тема читается на ней.
    /// </remarks>
    private void OnFix(object sender, RoutedEventArgs e)
    {
        foreach (var (slot, color) in ThemeFixer.Fix(_colors))
            _colors[slot] = color with { A = _colors[slot].A };

        if (_image is not null)
        {
            _ready = false;

            while (Themes.Preview(Preview()).Count > 0 && DimChoice.SelectedIndex < Dims.Length - 1)
                DimChoice.SelectedIndex++;

            _ready = true;
        }

        Changed();
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Картинка или видео фона",

            // Видео — живой фон по кругу (07.10); стекла под карточками у него нет.
            Filter = "Картинки и видео (PNG, JPG, MP4)|*.png;*.jpg;*.jpeg;*.mp4|Картинки (PNG, JPG)|*.png;*.jpg;*.jpeg|Видео (MP4)|*.mp4",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        bool video = Path.GetExtension(dialog.FileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        long limit = video ? ThemeLoader.MaxVideoBytes : ThemeLoader.MaxImageBytes;

        if (new FileInfo(dialog.FileName).Length > limit)
        {
            Say($"{(video ? "Видео" : "Картинка")} больше {limit / 1024 / 1024} МБ — возьмите поменьше.");
            return;
        }

        _image = dialog.FileName;

        // Новая картинка — новый кадр: прежнее место к ней не относится.
        _placed = false;
        ShowImage();
        Changed();
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        _image = null;
        ShowImage();
        Changed();
    }

    private void Say(string text)
    {
        Problems.Text = text;
        Problems.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Записывает тему и применяет её.
    /// </summary>
    /// <remarks>
    /// Нечитаемая записывается только по согласию и никогда поверх рабочей:
    /// правка рабочей темы, которая не читается, уходит копией. Так было
    /// с Nirvana 24.09 — перезаписанная красным фоном, она перестала
    /// применяться вовсе.
    /// </remarks>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameField.Text.Trim();

        if (name.Length == 0)
        {
            Say("Нужно название.");
            return;
        }

        var problems = Themes.Preview(Preview());
        var target = _editing;

        if (problems.Count > 0)
        {
            var answer = MessageBox.Show(
                "Тема не читается и не применится:\n\n"
                + string.Join("\n", problems.Take(4).Select(p => "• " + p.Replace("не читается: ", string.Empty)))
                + "\n\nСохранить её всё равно, чтобы доделать потом?"
                + (_editingWasFine ? " Рабочая тема не пострадает: правка ляжет копией." : string.Empty),
                "NetZapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            if (_editingWasFine)
            {
                target = null;
                SavedAsCopy = true;
            }
        }

        var draft = new ThemeDraft
        {
            Name = SavedAsCopy ? name + " — черновик" : name,
            Colors = Draft(),
            Font = ChosenFont,
            BackgroundSource = _image,
            Fit = (BackgroundFit)Math.Max(0, FitChoice.SelectedIndex),
            Dim = Dims[Math.Max(0, DimChoice.SelectedIndex)],
            Blur = Blurs[Math.Max(0, BlurChoice.SelectedIndex)],
            HomeX = _placed ? _homeX : null,
            HomeY = _placed ? _homeY : null,
            HomeSize = _placed ? _homeSize : null,
            HomeFullWidth = _homeWide,
        };

        try
        {
            var id = ThemeWriter.Save(draft, target);

            // Нечитаемая не применяется: остаётся то, что было до редактора.
            var result = problems.Count == 0 ? Themes.Apply(id) : Themes.Apply(_original);

            if (problems.Count == 0 && result.Ok)
                (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id }).Save(AppSettings.DefaultPath);

            SavedId = id;
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Say("Не удалось сохранить: " + ex.GetBaseException().Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Закрыли без сохранения — вернуть тему, что была до редактора.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_saved)
            Themes.Apply(_original);
    }
}
