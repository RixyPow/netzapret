using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Themes;
using NetZapret.Core.Updates;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Строка про один движок.</summary>
/// <param name="Word">Отметка в карточке одним-двумя словами (EngineHealth.Word).</param>
/// <param name="Detail">Строка под ней: причина надзора, когда движок не в порядке.</param>
public sealed record EngineRow(string Name, string Word, string Detail, Brush Color, int? ProcessId = null);

/// <summary>Предупреждение, которое стоит прочитать до запуска.</summary>
public sealed record WarningRow(string Title, string Body);

/// <summary>
/// Состояние настройки и движков, плюс запуск и остановка.
/// </summary>
/// <remarks>
/// <para>
/// Читает то же, что читает консоль, и теми же библиотеками:
/// <see cref="AppSettings"/>, <see cref="SupervisorState"/>,
/// <see cref="HostsEditor"/>, <see cref="SecuritySoftware"/>. Ни одна строка
/// в <c>src\</c> ради этого не тронута.
/// </para>
/// <para>
/// Запуск и остановка идут отдельным процессом — этой же программой
/// с ключом супервизора. Это не лень:
/// супервизор должен пережить закрытие окна, а значит быть отдельным
/// процессом. Консоль поступает ровно так же, запуская саму себя с ключом
/// <c>start</c>, — и раз путь один, у окна и меню не разойдётся поведение.
/// </para>
/// </remarks>
public partial class StatusView : UserControl
{
    /// <summary>Обычный период опроса.</summary>
    private static readonly TimeSpan CalmInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Период опроса во время запуска: подпись фазы и счётчик секунд должны
    /// успевать за движками, иначе окно выглядит замершим ровно тогда, когда
    /// на него и смотрят.
    /// </summary>
    private static readonly TimeSpan StartingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Сколько показывать запуск, прежде чем показать состояние как есть.
    /// </summary>
    /// <remarks>
    /// С запасом больше суммы таймаутов готовности обоих движков и первых
    /// перезапусков: объявить «не поднялось» раньше этого срока значило бы
    /// соврать про ещё идущую работу.
    /// </remarks>
    private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(90);

    private readonly DispatcherTimer _refresh = new() { Interval = CalmInterval };

    private DateTimeOffset? _startingSince;

    private void OnTelegram(object sender, RoutedEventArgs e) => OpenLink(About.Telegram);

    private void OnGitHub(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    /// <summary>Описание — README на GitHub: он и есть документация, другой нет.</summary>
    private void OnDocs(object sender, RoutedEventArgs e) => OpenLink(About.Repository + "#readme");

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера по умолчанию — те же ссылки есть в «Ещё», в карточке «О программе».
        }
    }

    private void OnArtSize(object sender, SizeChangedEventArgs e) => PlaceArt();

    /// <summary>Уже этой карточку с картинкой не показываем: в ней была бы полоска, а не персонаж.</summary>
    private const double ArtTileMin = SideArtLayout.TileMin;

    /// <summary>Затемнение над персонажем: текста на нём нет, читаемость беречь незачем.</summary>
    private const double ArtDim = 0.12;

    /// <summary>
    /// Кладёт картинку темы по странице и показывает её сквозь карточку справа.
    /// </summary>
    /// <remarks>
    /// <para>
    /// С 20.10 картинка — в карточке (владелец: «может для главной всё-таки
    /// место под картинку сделать тайлом?» — «делай»). Уложена она по-прежнему
    /// по всей странице: руками из редактора тем — долями страницы
    /// (<see cref="SideArtLayout.Place"/>), иначе — по высоте страницы
    /// серединой посередине карточки (<see cref="SideArtLayout.Fit"/>). Видна
    /// только в рамке: слой обрезан по ней. Так уложенное в темах до карточки
    /// не съезжает — персонаж стоит там же, где стоял в колонке справа.
    /// </para>
    /// <para>
    /// Под карточками картинки больше нет. До 20.10 там лежал её край под
    /// затемнением темы, сходившим к колонке почти на нет, а края картинки
    /// растворялись в подложку: у «Trident» светлая картина обрывалась
    /// вертикальной чертой посреди карточек (владелец, 30.09). У рамки края
    /// свои, скруглённые, — растворять нечего.
    /// </para>
    /// <para>
    /// Узкое окно (рамка уже 200) — карточки нет вовсе, как прежде не было
    /// колонки: полоска арта в несколько точек читалась ошибкой отрисовки
    /// (снимок 30.09). Нет картинки у темы — нет и пустой рамки.
    /// </para>
    /// </remarks>
    private void PlaceArt()
    {
        double width = ArtLayer.ActualWidth, height = ArtLayer.ActualHeight;

        // Места под карточку с картинкой нет — колонка содержимого во всю ширину
        // (12.11, снимок владельца: неразвёрнутое окно, карточки нет, а колонка
        // держит свои 940 — справа пустая полоса в сотню точек). Решает ширина
        // страницы (слой арта — на обе колонки), а не колонок — иначе правка
        // колонок меняла бы и само решение. Та же раскладка, что у миниатюры
        // в редакторе тем (SideArtLayout.Tile).
        if (ArtLayer.Visibility == Visibility.Visible && width > 0)
        {
            bool room = SideArtLayout.Tile(width, height) is not null;
            double max = room ? SideArtLayout.ContentMax : double.PositiveInfinity;

            if (ContentColumn.MaxWidth != max)
            {
                ContentColumn.MaxWidth = max;
                ArtColumn.Width = room ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
        }

        // Арт — картинка темы или её живой фон: видео в словаре ресурсов
        // не лежит (BackdropVideo), его рисунок берётся оттуда, а пропорции —
        // у кадра.
        (Size Size, Func<TileBrush> Make)? art = TryFindResource("SideArt") is ImageBrush { ImageSource: { } image }
            ? (new Size(image.Width, image.Height), () => new ImageBrush(image))
            : BackdropVideo.Current is { } video
                ? (video.Size, () => video.Brush())
                : null;

        var tile = ArtTile.ActualWidth >= ArtTileMin && ArtTile.ActualHeight > 0
            ? new Rect(ArtTile.TranslatePoint(new Point(0, 0), ArtLayer), ArtTile.RenderSize)
            : Rect.Empty;

        if (art is not { } content || width <= 0 || height <= 0 || tile.IsEmpty
            || content.Size.Height <= 0 || content.Size.Width <= 0)
        {
            Art.Fill = null;
            ArtGround.Fill = null;
            ArtShade.Fill = null;
            ArtTile.Visibility = Visibility.Hidden;
            return;
        }

        ArtTile.Visibility = Visibility.Visible;

        // Обрезка — по внутреннему краю рамки (2 точки): картинка уходит
        // под рамку на точку, и между ними не просвечивает фон.
        var inner = tile;
        inner.Inflate(-1, -1);
        ArtLayer.Clip = new RectangleGeometry(inner, 13, 13);

        double aspect = content.Size.Width / content.Size.Height;

        // Уложенная руками на миниатюре в редакторе тем (владелец 15.10) —
        // где положили, а под ней градиент; иначе по высоте страницы,
        // серединой посередине карточки.
        var placement = TryFindResource(HomePlacement.Key) as HomePlacement;

        var (left, top, w, h) = placement is { } placed
            ? SideArtLayout.Place(width, height, aspect, placed.X, placed.Y, placed.Size)
            : SideArtLayout.Fit(width, height, tile.Left + tile.Width / 2, aspect);

        var fill = content.Make();
        fill.Stretch = Stretch.Fill;
        fill.TileMode = TileMode.None;
        fill.ViewportUnits = BrushMappingMode.Absolute;
        fill.Viewport = new Rect(left, top, w, h);
        Art.Fill = fill;

        ArtGround.Fill = placement is null ? null : TryFindResource(HomePlacement.GroundKey) as Brush;
        Art.OpacityMask = placement is null ? null : HomePlacement.Feather(new Rect(left, top, w, h));

        var backdrop = TryFindResource("BackdropColor") is Color c ? c : Colors.Black;
        ArtShade.Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(ArtDim * 255), backdrop.R, backdrop.G, backdrop.B));
    }

    /// <summary>
    /// Плитки быстрых действий — всегда в ряд; на узком окне без подписи,
    /// с заголовком в две строки. Там же прячется полная строка внизу.
    /// </summary>
    /// <remarks>
    /// С подписью и значком (макет 19.10) плитке нужно около 205 точек: на окне
    /// в 1000 точек в ряд выходило «Проверить бл…» и «Открыть журн…» (снимок
    /// из теста). До 19.10 плитки на узком окне вставали по две, и «Главная»
    /// в не развёрнутом окне владельца (около 1067×712) уходила под прокрутку:
    /// снимок из теста — страница 755 точек при видимых 649. Строка внизу
    /// повторяет состояние, режим и пресет, видные выше, — на узком окне
    /// она не нужнее, чем место под ней. Решается по ширине самой сетки,
    /// а не окна: её сужает и масштаб. Только в подробном виде — в простом
    /// плиток нет, а строка внизу — одна нагрузка.
    /// </remarks>
    private void OnActionsSize(object sender, SizeChangedEventArgs e)
    {
        _narrow = e.NewSize.Width < NarrowActions;

        foreach (var tile in new[] { ActionCheck, ActionLog, ActionDoctor, ActionConfig })
        {
            if (tile.Content is HomeAction action)
                action.Compact = _narrow;
        }

        ShowFooterRow();
    }

    /// <summary>Уже этой ширины сетки плитки теряют подпись.</summary>
    private const double NarrowActions = 850;

    /// <summary>Сетка быстрых действий уже <see cref="NarrowActions"/>.</summary>
    private bool _narrow;

    private void OnQuickCheck(object sender, RoutedEventArgs e) => Open("check");

    private void OnQuickLog(object sender, RoutedEventArgs e) => LogWindow.Open();

    private void OnQuickDoctor(object sender, RoutedEventArgs e) => Open("doctor");

    private void OnQuickConfig(object sender, RoutedEventArgs e) => OpenConfigFolder();

    private void OnDesyncSettings(object sender, RoutedEventArgs e) => Open("desync");

    /// <summary>Серверы и подписки — вкладка VPN.</summary>
    private void OnServerChange(object sender, RoutedEventArgs e) => Open("vpn");

    private void Open(string section) => (Window.GetWindow(this) as MainWindow)?.Open(section);

    /// <summary>
    /// Папка настроек в проводнике, с выделенным файлом.
    /// </summary>
    /// <remarks>
    /// Папку, а не файл в блокноте: окно работает от администратора, и блокнот,
    /// открытый им, тоже был бы администраторским — правка настроек мимо окна
    /// с полными правами без нужды. Проводник открывается от человека.
    /// </remarks>
    private void OpenConfigFolder()
    {
        try
        {
            var file = Path.GetFullPath(AppSettings.DefaultPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { File.Exists(file) ? "/select," + file : Path.GetDirectoryName(file)! },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            ShowProblem("Проводник не открылся: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>То же окно, что у шестерёнки на вкладке VPN.</summary>
    private void OnTunnelSettings(object sender, RoutedEventArgs e)
    {
        var window = new TunnelSettingsWindow { Owner = Window.GetWindow(this) };

        window.ShowDialog();

        if (window.Changed)
        {
            // И режим: «Игнорировать исключения» живёт в этом окне и меняет
            // пояснение «Гибрида» и «Туннеля», а ShowModes зовётся не по времени.
            ShowModes(AppSettings.Load(AppSettings.DefaultPath));
            Update();
            this.Offer("Настройки туннеля изменены");
        }
    }

    /// <summary>Вид, заданный снаружи (тесты), — мимо настроек.</summary>
    private readonly bool? _forcedView;

    /// <summary>Подробный вид «Главной» (AppSettings.HomeAdvanced).</summary>
    private bool _advanced;

    /// <summary>Раздел в заданном виде, а не в том, что в настройках, — для тестов раскладки.</summary>
    internal StatusView(bool advanced)
        : this()
    {
        _forcedView = advanced;
    }

    public StatusView()
    {
        InitializeComponent();

        // Высота карточки состояния простого вида — по высоте окна (StateHeight).
        SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && !_advanced)
                StateGrid.Height = StateHeight(e.NewSize.Height);
        };

        // Опрос по времени, а не подписка на события: супервизор — отдельный
        // процесс и пишет своё состояние в файл. Две секунды достаточно,
        // чтобы нажатие «Запустить» отозвалось раньше, чем человек усомнится.
        _refresh.Tick += (_, _) => Update();

        // Опрос — только пока раздел на экране. В свёрнутом окне он шёл как
        // в открытом: каждые две секунды чтение настроек и состояния, замер
        // процессов движков и два запроса к Clash API — ради строк, которых
        // никто не видит (09.11). Вернулись — читаем сразу, не дожидаясь тика.
        OnScreen.Watch(this, seen =>
        {
            if (!seen)
            {
                _refresh.Stop();
                return;
            }

            // Через очередь окна, а не прямо отсюда: Update ждёт чтение в пуле
            // и возвращается в поток окна по его контексту, а смена видимости
            // приходит и вне операции очереди (Show из кода) — тогда контекста
            // нет, и продолжение ушло бы в пул, к элементам окна из чужого потока.
            Dispatcher.BeginInvoke(new Action(Update));
            _refresh.Start();
        });

        Loaded += (_, _) =>
        {
            // Не по времени, в отличие от прочего: режим и вид меняются только
            // отсюда, а автозапуск спрашивается у планировщика запуском schtasks —
            // раз в две секунды это был бы процесс на ровном месте.
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            ApplyView(_forcedView ?? settings.HomeAdvanced, settings);
            ShowWidth(settings);
            ShowSupport(settings);

            // Проверка обновлений идёт при запуске окна и может закончиться
            // уже после того, как «Главная» показана, — поэтому и подписка.
            // Отписка обязательна: событие статическое, а раздел пересоздаётся
            // при каждом возврате.
            UpdateNotice.Changed += OnUpdateChanged;
            ShowUpdate();
        };

        Unloaded += (_, _) => UpdateNotice.Changed -= OnUpdateChanged;
    }

    private void OnUpdateChanged() => Dispatcher.InvokeAsync(ShowUpdate);

    private void ShowSupport(AppSettings settings) =>
        SupportCard.Visibility = NetZapret.Core.Updates.SupportAsk.Shown(settings)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Звезда ставится на странице репозитория — на неё и ведём.</summary>
    private void OnStar(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    private void OnSupportHide(object sender, RoutedEventArgs e)
    {
        SupportCard.Visibility = Visibility.Collapsed;

        try
        {
            NetZapret.Core.Updates.SupportAsk.Hide(AppSettings.Load(AppSettings.DefaultPath))
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — карточка вернётся при следующем запуске, беды в том нет.
        }
    }

    /// <summary>Карточка «вышла новая версия» — если есть что и о нём не сказали «не сейчас».</summary>
    private void ShowUpdate()
    {
        var release = UpdateNotice.Available;

        if (!UpdateNotice.ShouldOffer(release, AppSettings.Load(AppSettings.DefaultPath)))
        {
            UpdateCard.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateTitle.Text = $"Вышла {release!.Version}";

        var highlights = UpdateNotice.Highlights(release.Notes);

        UpdateWhat.Text = highlights.Count > 0
            ? string.Join(" · ", highlights) + "."
            : "Установлена " + UpdateCheck.Current + ".";

        UpdateCard.Visibility = Visibility.Visible;
    }

    // Установка та же, что в блоке «Обновление» ниже, и на той же странице:
    // прежде кнопка уводила в «Ещё» ради одного вопроса «обновить?».
    private void OnUpdateNow(object sender, RoutedEventArgs e) => Updates.Install();

    // «Что нового» — окно обновления (владелец, 06.10), а не страница выпуска:
    // там примечания всех пропущенных версий, а браузер — одной кнопкой в нём.
    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        (Window.GetWindow(this) as MainWindow)?.ShowUpdateWindow(release);
    }

    /// <summary>Перечитать карточку обновления — после «Пропустить версию» в окне.</summary>
    internal void RefreshUpdate() => ShowUpdate();

    /// <summary>
    /// «Не сейчас»: об этой версии больше не напоминать. Точка у «Ещё»
    /// остаётся — она тихая и уходит, только когда версия поставлена.
    /// </summary>
    private void OnUpdateLater(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { DismissedUpdate = release.Version })
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — напомним при следующем запуске, беды в том нет.
        }

        UpdateCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>То, что «Главная» читает с диска и у системы на каждом тике.</summary>
    /// <param name="Problem">Настройки не прочитались — почему; тогда прочее пусто.</param>
    /// <param name="SingBoxUp">Есть ли процесс sing-box — для фазы запуска, только пока он идёт.</param>
    /// <param name="Subscriptions">Счёт подписок — для подробного вида; в простом пусто.</param>
    /// <param name="ConfigBuilt">Когда собран конфиг туннеля — для подробного вида.</param>
    private sealed record Seen(
        AppSettings? Settings,
        string? Problem,
        SupervisorState? State,
        bool Running,
        bool SingBoxUp,
        bool Coming = false,
        string PresetNote = "",
        string PresetProblem = "",
        string Subscriptions = "",
        DateTime? ConfigBuilt = null);

    private static Seen Read(bool starting, bool advanced)
    {
        AppSettings settings;

        try
        {
            settings = AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            return new Seen(null, $"Настройки не читаются: {ex.Message}", null, false, false);
        }

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        var (presetNote, presetProblem) = PresetNoteOf(settings.PresetName);

        // Ссылки на подписки — пароли, и в окне им не место: показываем лишь
        // счёт. Время файла конфига — время, когда взяты серверы подписок:
        // конфиг собирается при каждом запуске. Оба — только подробному виду.
        string subscriptions = string.Empty;
        DateTime? built = null;

        if (advanced)
        {
            try
            {
                subscriptions = SubscriptionBook.Load().Describe(settings);
            }
            catch (Exception)
            {
            }

            try
            {
                var config = new FileInfo(Path.Combine("runtime", "singbox.json"));

                if (config.Exists)
                    built = config.LastWriteTime;
            }
            catch (Exception)
            {
            }
        }

        return new Seen(settings, null, state, running, starting && !running && IsRunning("sing-box"),
            !running && EngineControl.Coming, presetNote, presetProblem, subscriptions, built);
    }

    private bool _updating;
    private bool _updateAgain;

    /// <summary>
    /// Перечитывает состояние в пуле и показывает его.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Чтение — не в потоке окна. Сторож подвисаний 01.10 писал на «Главной»
    /// при запуске движков 300–860 мс «занят» при 0–80 мс процессора: поток
    /// окна ждал. В покое замер того же чтения — единицы миллисекунд, но
    /// в нём есть ожидания: занятый файл настроек читается повтором через
    /// 60 мс до пяти раз, процессы перебираются системой, а при запуске
    /// файлы пишут сразу надзор и движки. Какое из ожиданий подвешивало
    /// окно, не выяснено — уведены все.
    /// </para>
    /// <para>
    /// Вызов посреди идущего чтения не теряется и не множит чтения:
    /// отмечается, и чтение повторяется по окончании.
    /// </para>
    /// </remarks>
    private async void Update()
    {
        if (_updating)
        {
            _updateAgain = true;
            return;
        }

        _updating = true;

        try
        {
            do
            {
                _updateAgain = false;

                bool starting = _startingSince is not null;
                bool advanced = _advanced;
                Show(await Task.Run(() => Read(starting, advanced)));
            }
            while (_updateAgain);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Show(Seen seen)
    {
        if (seen.Settings is not { } settings)
        {
            ShowProblem(seen.Problem!);
            return;
        }

        Problem.Visibility = Visibility.Collapsed;

        PresetValue.Text = settings.DescribePreset();
        // Описание пресета — строкой под именем, как до 08.11: в подсказке «i» его
        // не видно, а им пресеты и различают (владелец вернул в тот же день).
        // Беда с пресетом — на том же месте вместо описания.
        // В простом виде описания нет (владелец 11.11), беда — есть.
        string presetLine = seen.PresetProblem.Length > 0 ? seen.PresetProblem : _advanced ? seen.PresetNote : string.Empty;
        PresetNote.Text = presetLine;
        PresetNote.Visibility = presetLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowServer(settings, _exit);
        ShowCards(settings);
        SetRouterDnsWarning(settings);

        if (_advanced)
        {
            DnsValue.Text = settings.UseRouterDns
                ? RouterDnsConnection.ConfigWarning(settings) is not null
                    ? $"Запасной DoH · {settings.DnsServer} (DNS роутера недоступен)"
                    : $"Роутер · {settings.RouterDnsAddress}:53 · UDP"
                : settings.DnsCryptActive is { } crypt ? $"{DnsCrypt.Describe(crypt).Title} · dnscrypt-proxy"
                : settings.DnsServer;
            SubscriptionValue.Text = seen.Subscriptions;
        }

        var state = seen.State;
        bool running = seen.Running;

        // Движки поднимает не эта кнопка — перезапуск из всплывающего
        // сообщения, трей, автозапуск: запуск показывается и тогда.
        if (_startingSince is null && seen.Coming)
        {
            _startingSince = DateTimeOffset.Now;
            _refresh.Interval = StartingInterval;
        }

        ShowState(settings, state, running, seen.SingBoxUp, seen.Coming);
        ShowModeLine();

        if (_advanced)
            ShowFooter(settings, state, running, seen.ConfigBuilt);

        ShowWarnings(settings);

        bool tunnel = running && state!.Services.Any(s => s.Name == "sing-box");

        _ = ReadExitAsync(settings, tunnel);

        if (running)
        {
            _ = ReadMetersAsync(state, tunnel);
        }
        else
        {
            // Счётчики видны всегда (владелец 19.10) — у остановленных движков прочерками.
            UpValue.Text = DownValue.Text = CpuValue.Text = RamValue.Text = "—";
            CpuMeter.ToolTip = RamMeter.ToolTip = "Движки не запущены.";
        }
    }

    /// <summary>Выход, который держит движок; <c>null</c> — не знаем или туннеля нет.</summary>
    private string? _exit;

    private bool _readingExit;

    /// <summary>
    /// Спрашивает у движка, через какой сервер идёт трафик, — для строки «Сервер».
    /// </summary>
    /// <remarks>
    /// Владелец 26.09: «нигде так и не написано, какой сервер активен». Строка
    /// говорила «авто (по задержке)» — то, что в настройках, а не то, чем идёт
    /// трафик. Спрашиваем движок, как nz status: 23.09 настройки говорили
    /// «авто», а движок держался WARP из кэша. Прежний ответ держится до нового,
    /// чтобы строка не мигала на каждом тике.
    /// </remarks>
    private async Task ReadExitAsync(AppSettings settings, bool tunnel)
    {
        if (!tunnel)
        {
            _exit = null;
            return;
        }

        if (_readingExit)
            return;

        _readingExit = true;

        try
        {
            // В пуле целиком: до первого ожидания запрос идёт синхронно
            // (клиент, поиск прокси системы), а ответ пишется в журнал
            // недавних выходов — файлом.
            var (server, _) = await Task.Run(() => TunnelStatus.CurrentExitAsync(CancellationToken.None));

            if (server is null || server == _exit)
                return;

            _exit = server;
            ShowServer(settings, server);
        }
        finally
        {
            _readingExit = false;
        }
    }

    private void ShowState(AppSettings settings, SupervisorState? state, bool running, bool singBoxUp, bool coming)
    {
        // Запуск показывается своим чередом: пока он идёт, «остановлено»
        // означает не отказ, а то, что супервизор ещё не дописал состояние.
        if (_startingSince is not null && ShowStarting(settings, state, running, singBoxUp, coming))
            return;

        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ShowEngineButtons(running);

        if (!running)
        {
            ShowDot("Faint");
            ShowWave("Faint", idle: true);

            StateLine.Text = "Остановлено";

            StateHint.Text = settings.NeedsProxy || settings.NeedsDesync
                ? "Обход не работает: трафик идёт напрямую."
                : "В этом режиме запускать нечего: VPN выключен, пресет не выбран.";

            StartButton.IsEnabled = settings.NeedsProxy || settings.NeedsDesync;

            // Всё остановлено — так и сказано карточкой состояния; строки
            // движков под режимами нет (с 11.11).
            _enginesTrouble = false;
            SetEngines(Planned(settings, "остановлен", "Faint"));
            SetDnsWarning(null);

            return;
        }

        var services = state!.Services;

        // Резолвер, который не отвечает, — не «Работает»: 25.10 у владельца умер
        // Comss DNS, каждое имя ждало 11–12 с, «всё виснет», а «Главная» была зелёной.
        bool dnsDown = services.Any(s => s.DnsDown);
        bool healthy = services.All(s => s.Health == ServiceHealth.Healthy) && !dnsDown;

        SetDnsWarning(services.FirstOrDefault(s => s.DnsNotice is { Length: > 0 }) is { } dns
            ? DnsWarning(dns)
            : SystemDnsWarning(settings, state!));

        // Три состояния, а не два. «Оговорки» бывают разной тяжести: движок,
        // которому не удалась глубокая проверка, ещё несёт трафик, а умерший
        // не несёт ничего. Одним жёлтым это выглядело одинаково, и разницу
        // приходилось искать в списке ниже.
        bool broken = services.Any(s =>
            s.Health is ServiceHealth.Dead or ServiceHealth.Faulted);

        var colour = healthy ? "Accent" : broken ? "Danger" : "Warn";

        ShowDot(colour);
        ShowWave(colour);

        StateLine.Text = healthy
            ? "Работает"
            : broken ? "Движок не работает" : "Работает с оговорками";

        StateHint.Text = healthy
            ? $"Запущено {Ago(state.StartedAt)}."
            : broken
                ? "Один из движков не запущен — обход работает не полностью. Подробности ниже."
                : dnsDown
                    ? "Резолвер движка не отвечает — сайты открываются с задержкой или не открываются. Подробности ниже."
                    : "Часть движков не в порядке — подробности ниже.";

        // Причина упавшего движка — целиком строкой ниже, а не только подсказкой
        // его строки: та в одну строку и обрезается. 22.10 у знакомого владельца
        // «кодом 433» с объяснением, что делать, было видно лишь при наведении.
        if (broken && Failures(services) is { Length: > 0 } failures)
            ShowProblem(failures);

        _enginesTrouble = services.Any(s => s.Health != ServiceHealth.Healthy);
        SetEngines(services.Select(Row).ToList());
    }

    /// <summary>
    /// Показывает ход запуска. Возвращает <c>false</c>, когда показывать
    /// больше нечего и состояние пора рисовать обычным путём.
    /// </summary>
    /// <remarks>
    /// Умерший движок здесь не считается концом: супервизор его перезапустит,
    /// и до тех пор запуск продолжается. Концом считается только «сдался»
    /// либо исчерпанное терпение — иначе окно объявляло бы отказ, пока
    /// внизу ещё идут попытки.
    /// </remarks>
    private bool ShowStarting(AppSettings settings, SupervisorState? state, bool running, bool singBoxUp, bool coming)
    {
        var since = _startingSince!.Value;
        var services = running ? state!.Services : [];

        bool ready = services.Count > 0 && services.All(s => s.Health == ServiceHealth.Healthy);
        bool gaveUp = services.Any(s => s.Health == ServiceHealth.Faulted);

        // Ни состояния, ни живого супервизора, ни запуска в очереди — значит,
        // он ушёл, не подняв движков. Ждать тут все 90 с незачем.
        bool gone = !running && !coming && DateTimeOffset.Now - since > TimeSpan.FromSeconds(3);

        if (ready || gaveUp || gone || DateTimeOffset.Now - since > StartupPatience)
        {
            EndStarting();
            return false;
        }

        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        ShowEngineButtons(true);

        ShowDot("Warn");
        ShowWave("Warn", starting: true);

        StateLine.Text = "Запускается…";

        var seconds = (int)(DateTimeOffset.Now - since).TotalSeconds;
        // Неразрывный пробел: в узкой колонке над волной «с» уезжала на свою строку.
        StateHint.Text = $"{DescribePhase(settings, running, singBoxUp, services)} — {seconds}\u00A0с";

        // Пока супервизор не дописал состояние, движки показываются жёлтыми
        // и «запускается». Прежде список опустошался, и они пропадали ровно
        // на те десятки секунд, когда на них и смотрят: раздел отвечал «их
        // нет» на вопрос «поднимаются ли они».
        _enginesTrouble = true;
        SetEngines(services.Count > 0
            ? services.Select(Row).ToList()
            : Planned(settings, "запускается", "Warn"));

        return true;
    }

    /// <summary>
    /// Чем занят запуск прямо сейчас.
    /// </summary>
    /// <remarks>
    /// До появления состояния фаза определяется по живым процессам, а не по
    /// файлу: супервизор пишет состояние впервые лишь после того, как поднял
    /// все службы, — то есть ровно после окончания промежутка, который здесь
    /// и показывается. Файл в это время либо отсутствует, либо остался от
    /// прошлого запуска.
    /// </remarks>
    private static string DescribePhase(
        AppSettings settings,
        bool running,
        bool singBoxUp,
        IReadOnlyList<ServiceState> services)
    {
        if (!running)
        {
            if (!singBoxUp)
                return "Собираем конфиг и поднимаем супервизор";

            // Проверка прохода трафика уходит в сеть и занимает основную часть
            // ожидания, поэтому названа отдельно: иначе эти секунды выглядят
            // как необъяснённая пауза.
            return settings.VerifyTraffic
                ? "Туннель поднимается, проверяем проход трафика"
                : "Туннель поднимается";
        }

        var singBox = services.FirstOrDefault(s => s.Name == "sing-box");

        return singBox?.Health switch
        {
            ServiceHealth.Degraded => "Туннель поднят, проверка ещё не прошла",
            ServiceHealth.Dead => "Туннель не поднялся, идёт перезапуск",
            _ => "Движки поднимаются",
        };
    }

    private static bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);

        // Каждый Process держит системный дескриптор, а опрос идёт раз
        // в секунду: без освобождения они копятся всё время запуска.
        foreach (var process in found)
            process.Dispose();

        return found.Length > 0;
    }

    private void BeginStarting()
    {
        _startingSince = DateTimeOffset.Now;
        _refresh.Interval = StartingInterval;

        Update();
    }

    private void EndStarting()
    {
        _startingSince = null;
        _refresh.Interval = CalmInterval;
    }

    // --- Волна туннельного эффекта (владелец 05.11) --------------------------------

    /// <summary>
    /// Состояние волны на карточке: цвет, доля прохода сквозь барьер, мерцание.
    /// </summary>
    /// <remarks>
    /// Цвет — ключ состояния: тот же, что у слова «Работает» и у движков
    /// в строке под режимами. Проход — смысл картинки: работает —
    /// заметная часть волны проходит, с оговорками — меньше, упал — ничего;
    /// стоит — волн нет вовсе, ровная линия у барьера.
    /// </remarks>
    private void ShowWave(string colourKey, bool idle = false, bool starting = false)
    {
        double pass = colourKey switch
        {
            "Accent" => 0.42,
            "Warn" => starting ? 0.3 : 0.22,
            _ => 0,
        };

        Wave.Show(
            StateColour(colourKey),
            StateColour("Muted"),
            pass,
            idle,
            flicker: starting,
            broken: colourKey == "Danger");
    }

    /// <summary>
    /// «Запустить» — залитая кнопка, «Остановить» — рамкой (05.11): у работающего
    /// обхода главное на карточке — волна, а остановку жмут редко.
    /// </summary>
    private void ShowEngineButtons(bool running)
    {
        EngineButtons.SetResourceReference(Border.BackgroundProperty, running ? "Raised" : "AccentFill");
        EngineButtons.SetResourceReference(Border.BorderBrushProperty, running ? "Border" : "AccentFill");

        var ink = running ? "Text" : "OnAccent";

        foreach (var part in new Control[] { StartButton, StopButton, EngineMenuButton })
            part.SetResourceReference(ForegroundProperty, ink);

        EngineButtonsSeam.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, running ? "Border" : "OnAccent");
        EngineButtonsSeam.Opacity = running ? 1 : 0.35;
    }

    /// <summary>
    /// Цвет состояния по ключу. Жёлтый — насыщенный (WarnFill): текстовый на
    /// светлой теме у тонкой линии выглядит выцветшей серостью, а не предупреждением.
    /// </summary>
    private Color StateColour(string colourKey) =>
        (FindResource(colourKey == "Warn" ? "WarnFill" : colourKey) as SolidColorBrush)?.Color ?? Colors.Gray;

    /// <summary>Прошлый замер трафика: из разницы — скорость, от неё — сила волны.</summary>
    private (long Total, DateTime At)? _moved;

    /// <summary>
    /// Сила волны по скорости трафика туннеля: 1 КБ/с — тихо, 10 МБ/с — во всю.
    /// </summary>
    /// <remarks>
    /// Шкала логарифмическая: между фоновым шумом и закачкой три порядка,
    /// и в прямой шкале волна почти всё время стояла бы у нуля. В «Десинке»
    /// туннеля нет и считать нечем — средняя сила, чтобы волна не замирала.
    /// </remarks>
    private void ShowTraffic((long Up, long Down)? totals)
    {
        if (totals is not { } moved)
        {
            _moved = null;
            Wave.Traffic = 0.4;
            return;
        }

        long total = moved.Up + moved.Down;
        var now = DateTime.UtcNow;

        if (_moved is { } before && now > before.At && total >= before.Total)
        {
            double rate = (total - before.Total) / (now - before.At).TotalSeconds;
            Wave.Traffic = Math.Clamp((Math.Log10(Math.Max(rate, 1)) - 3) / 4, 0.1, 1);
        }

        _moved = (total, now);
    }

    /// <summary>
    /// Карточка «Сервер»: имя выхода крупно, под ним — кто его выбрал, в плитке — флаг.
    /// </summary>
    /// <remarks>
    /// Выход спрашивается у движка (<see cref="ReadExitAsync"/>), а не берётся
    /// из настроек: там «авто», а трафик идёт через один определённый.
    /// Флаг — фоном плитки (FlagImages): Windows флагов не рисует, а картинка
    /// в рамке срезалась (флаги VPN, 08.11).
    /// </remarks>
    private void ShowServer(AppSettings settings, string? live)
    {
        var preferred = NetZapret.Subscriptions.Warp.PreferredExit(settings);
        var shown = live ?? preferred;

        ServerValue.Text = shown is null ? "Автоподбор" : TunnelStatus.NameOf(shown);

        // Кто выбрал — только про работающий выход: до запуска слово
        // говорило бы о том, чего ещё нет.
        var who = live is null
            ? string.Empty
            : TunnelStatus.Standing(live, preferred) switch
            {
                ExitStanding.Auto => "автоподбор",
                ExitStanding.Pinned => "выбран вручную",
                _ => "временная замена выбранному",
            };

        ServerNote.Text = who;
        ServerNote.Visibility = who.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var country = shown is null ? string.Empty : NetZapret.Subscriptions.CountryTag.Split(shown).Country;
        var flag = country.Length == 2 ? FlagImages.For(country) : null;

        ServerFlag.Background = flag is null ? null : new ImageBrush(flag) { Stretch = Stretch.UniformToFill };
        ServerFlag.Visibility = flag is null ? Visibility.Collapsed : Visibility.Visible;
        ServerGlyph.Visibility = flag is null ? Visibility.Visible : Visibility.Collapsed;

        // Подробный вид — строкой «Сервер» в параметрах подключения, как до 11.11:
        // «Финляндия (авто)» — то, чем идёт трафик, и кто его выбрал.
        ParamsServer.Text = TunnelStatus.ServerLine(settings, live);
        ParamsFlag.Background = ServerFlag.Background;
        ParamsFlag.Visibility = ServerFlag.Visibility;
    }

    /// <summary>
    /// Пресет — когда режиму нужен десинк, сервер — когда нужен туннель; одна —
    /// во всю ширину.
    /// </summary>
    /// <remarks>
    /// До 11.11 обе стояли всегда: в «Туннеле» пресет ни на что не влияет,
    /// в «Десинке» сервера нет — карточка говорила о том, чего не будет.
    /// Режим не выбран — обе, как прежде: выбирать есть из чего.
    /// </remarks>
    private void ShowCards(AppSettings settings)
    {
        // Подробный вид — как до 11.11: пресет и параметры подключения при любом режиме.
        if (_advanced)
        {
            PresetCard.Visibility = Visibility.Visible;
            ServerCard.Visibility = Visibility.Collapsed;
            ParamsCard.Visibility = Visibility.Visible;
            CardsGap.Width = new GridLength(14);
            Grid.SetColumnSpan(PresetCard, 1);
            return;
        }

        ParamsCard.Visibility = Visibility.Collapsed;

        bool none = !settings.NeedsDesync && !settings.NeedsProxy;
        bool preset = settings.NeedsDesync || none;
        bool server = settings.NeedsProxy || none;

        PresetCard.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;
        ServerCard.Visibility = server ? Visibility.Visible : Visibility.Collapsed;

        bool both = preset && server;
        CardsGap.Width = new GridLength(both ? 14 : 0);
        Grid.SetColumnSpan(PresetCard, both ? 1 : 3);
        Grid.SetColumn(ServerCard, both ? 2 : 0);
        Grid.SetColumnSpan(ServerCard, both ? 1 : 3);
    }

    /// <summary>Нагрузка движков между замерами: доля ядра считается по разнице, поэтому замер один на окно.</summary>
    private static readonly EngineLoad Load = new();

    private bool _readingMeters;

    /// <summary>
    /// Счётчики под кнопкой: трафик движка туннеля с запуска, процессор и память движков.
    /// </summary>
    /// <remarks>
    /// В пуле: замер процессов и запрос к Clash API — ожидания, а поток окна
    /// на «Главной» уже ловили на ожиданиях (сторож подвисаний, 01.10).
    /// </remarks>
    private async Task ReadMetersAsync(SupervisorState? state, bool tunnel)
    {
        if (_readingMeters)
            return;

        _readingMeters = true;

        try
        {
            var (engines, totals) = await Task.Run(async () =>
            {
                var used = Load.Sample(state);
                (long Up, long Down)? moved = null;

                if (tunnel)
                {
                    using var api = new ClashApi();
                    moved = await api.TotalsAsync(CancellationToken.None);
                }

                return (used, moved);
            });

            // Туннеля нет («Десинк») или движок не ответил — прочерк, а не пропавшая колонка:
            // блок постоянной ширины (владелец 19.10), и колонки в нём не прыгают.
            UpValue.Text = totals is { } moved ? NetZapret.Subscriptions.SubscriptionUsage.Size(moved.Up) : "—";
            DownValue.Text = totals is { } back ? NetZapret.Subscriptions.SubscriptionUsage.Size(back.Down) : "—";

            ShowTraffic(totals);

            CpuValue.Text = EngineLoad.Cores(engines);
            RamValue.Text = engines.Count > 0 ? NetZapret.Subscriptions.SubscriptionUsage.Size(engines.Sum(e => e.Memory)) : "—";

            var explained = EngineLoad.Explain(engines, NetZapret.Subscriptions.SubscriptionUsage.Size);
            CpuMeter.ToolTip = explained;
            RamMeter.ToolTip = explained;
        }
        catch (Exception)
        {
            // Счётчики — показ, а не работа: не прочиталось — покажем в следующий раз.
        }
        finally
        {
            _readingMeters = false;
        }
    }

    /// <summary>
    /// Меню у «Остановить» (макет 19.10): перезапуск и оба выхода, как в трее.
    /// </summary>
    private void OnEngineMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = EngineMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var restart = new MenuItem { Header = "Перезапустить", IsEnabled = EngineControl.IsRunning };
        restart.Click += async (_, _) =>
        {
            BeginStarting();

            var outcome = await EngineControl.RestartAsync("меню у «Остановить» на «Главной»", CancellationToken.None);

            if (!outcome.Ok)
            {
                EndStarting();
                ShowProblem(outcome.Message);
                Update();
            }
        };
        menu.Items.Add(restart);

        // Журнал и папка настроек — здесь с 11.11: стояли плитками быстрых
        // действий внизу «Главной», вместе с переходами, которые повторяли меню.
        var log = new MenuItem { Header = "Открыть журнал" };
        log.Click += (_, _) => LogWindow.Open();
        menu.Items.Add(log);

        var config = new MenuItem { Header = "Открыть папку настроек", ToolTip = "Папка config в проводнике, файл настроек выделен" };
        config.Click += (_, _) => OpenConfigFolder();
        menu.Items.Add(config);

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        // Те же два выхода, что в трее (TrayMenu): «убрать окно» и «выключить
        // обход» — разные желания, и ошибка в любую сторону дорогая.
        var keep = new MenuItem { Header = "Выйти, движки остаются работать" };
        keep.Click += (_, _) => TrayIcon.Exit(stopEngines: false);
        menu.Items.Add(keep);

        var stop = new MenuItem { Header = "Выйти и остановить движки" };
        stop.SetResourceReference(ForegroundProperty, "Danger");
        stop.Click += (_, _) => TrayIcon.Exit(stopEngines: true);
        menu.Items.Add(stop);

        menu.IsOpen = true;
    }

    /// <summary>
    /// Полная строка внизу — подробный вид: состояние с длительностью, режим
    /// и пресет — и время сборки конфига туннеля.
    /// </summary>
    /// <remarks>
    /// Режим и пресет — по макету 19.10: внизу их видно одной строкой, не читая карточек.
    /// </remarks>
    private void ShowFooter(AppSettings settings, SupervisorState? state, bool running, DateTime? configBuilt)
    {
        var said = running && _startingSince is null
            ? $"{StateLine.Text} · {Span(DateTimeOffset.Now - state!.StartedAt)}"
            : StateLine.Text;

        FooterState.Text = $"{said} · Режим: {settings.DescribeMode()}"
            + (settings.NeedsDesync ? $" · Пресет: {settings.DescribePreset()}" : string.Empty);

        FooterConfig.Text = configBuilt is { } at
            ? $"Конфиг туннеля собран {at:dd.MM.yyyy} в {at:HH:mm}"
            : string.Empty;
    }

    /// <summary>Красит точку в полной строке внизу — тем же ключом, что волну (ShowWave).</summary>
    private void ShowDot(string colourKey) => FooterDot.Fill = (Brush)FindResource(colourKey);

    /// <summary>
    /// Строка внизу: в простом виде — одна нагрузка, всегда; в подробном —
    /// полная, и на узком окне её нет (OnActionsSize).
    /// </summary>
    private void ShowFooterRow()
    {
        FooterStatePanel.Visibility = _advanced ? Visibility.Visible : Visibility.Collapsed;
        FooterConfig.Visibility = _advanced ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = _advanced && _narrow ? Visibility.Collapsed : Visibility.Visible;

        // Простой вид (макет, 11.11): нагрузка слева, отчёт справа.
        Grid.SetColumn(FooterMeters, _advanced ? 1 : 0);
        ReportButton.Visibility = _advanced ? Visibility.Collapsed : Visibility.Visible;
        HomeUpdateButton.Visibility = ReportButton.Visibility;

        if (_advanced)
            ReportNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>«23 дня, 4 часа», «4 ч 12 мин», «7 мин».</summary>
    internal static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            int days = (int)span.TotalDays;
            return $"{days} {Plural(days, "день", "дня", "дней")}, {span.Hours} {Plural(span.Hours, "час", "часа", "часов")}";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} ч {span.Minutes} мин"
            : $"{Math.Max(0, span.Minutes)} мин";
    }

    private static string Plural(int count, string one, string few, string many)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return many;

        return (tail % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Описание пресета, уже прочитанное: файл со временем записи и первая фраза.</summary>
    private sealed record PresetNoteRead(string Key, string Note);

    private static PresetNoteRead? _presetNote;

    /// <summary>
    /// Первая фраза описания пресета — из его же файла, и беда с ним, если есть;
    /// зовётся в пуле (<see cref="Read"/>).
    /// </summary>
    /// <remarks>
    /// Разбор файла — сотни строк, а меняется выбор раз в неделю: файл читается
    /// один раз, пока не изменится. До 05.11 он читался в потоке окна при каждом
    /// заходе на «Главную»: память о показанном жила в разделе, а раздел
    /// создаётся заново на каждый заход. Симуляция 05.11 застала поток окна
    /// в <c>PresetReader.Read</c> посреди подвисания.
    /// </remarks>
    private static (string Note, string Problem) PresetNoteOf(string? preset)
    {
        if (preset is null)
            return (string.Empty, "Десинк без пресета не поднимается.");

        try
        {
            if (NetZapret.Zapret.ZapretPaths.FindPreset(preset) is not { } file)
                return (string.Empty, "Файла пресета нет в папке presets.");

            var key = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";

            if (_presetNote is { } known && known.Key == key)
                return (known.Note, string.Empty);

            // Нет описания — нет и подсказки: «описания нет» человеку ничего не даёт.
            var description = new NetZapret.Zapret.PresetReader().Read([file]).FirstOrDefault()?.Description;
            var note = FirstSentence(description) ?? string.Empty;

            _presetNote = new PresetNoteRead(key, note);
            return (note, string.Empty);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// До первой точки, двоеточия или тире с пробелом после — дальше в описаниях
    /// пресетов начинаются подробности.
    /// </summary>
    /// <remarks>
    /// С пробелом, а не любая точка: в описаниях стоят версии и имена
    /// («v1.0.3», «www.facebook.com»), и по голой точке фраза рвалась бы на них.
    /// </remarks>
    internal static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        var end = System.Text.RegularExpressions.Regex.Match(text, @"[.:](\s|$)|\s—\s");

        return end.Success && end.Index > 0 ? text[..end.Index].TrimEnd() + "." : text;
    }

    /// <summary>
    /// Выпадающий список пресетов у «Изменить».
    /// </summary>
    /// <remarks>
    /// Список тот же и в том же порядке, что в «Десинке» (PresetNewness), и запись
    /// та же — PresetName. Последним пунктом — сам раздел: там видно, чем
    /// пресеты отличаются, а здесь только имена.
    /// </remarks>
    private void OnPresetMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PresetMenu, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string? chosen;

        try
        {
            chosen = AppSettings.Load(AppSettings.DefaultPath).PresetName;

            var presets = NetZapret.Zapret.PresetNewness.Order(
                new NetZapret.Zapret.PresetReader().Read(NetZapret.Zapret.ZapretPaths.PresetFiles),
                preset => Path.GetFileNameWithoutExtension(preset.FilePath),
                preset => preset.BuiltinVersion);

            foreach (var preset in presets)
            {
                var name = Path.GetFileNameWithoutExtension(preset.FilePath);
                var item = new MenuItem { Header = name, Tag = name };

                if (string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase))
                    item.Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = (Brush)FindResource("Accent") };

                item.Click += (_, _) => ChoosePreset(name);
                menu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Пресеты не читаются: " + ex.GetBaseException().Message);
            return;
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        var all = new MenuItem { Header = "Все пресеты — раздел «Десинк»" };
        all.Click += (_, _) => Open("desync");
        menu.Items.Add(all);

        menu.IsOpen = true;
    }

    private void ChoosePreset(string name)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (string.Equals(settings.PresetName, name, StringComparison.OrdinalIgnoreCase))
                return;

            (settings with { PresetName = name }).Save(AppSettings.DefaultPath);

            Update();
            this.Offer($"Выбран пресет «{name}»");
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать выбор: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Движки, которые поднимутся при нынешней настройке.
    /// </summary>
    /// <remarks>
    /// Список тот же, что собирает супервизор: туннель — когда режим ведёт
    /// хоть что-то через VPN, десинк — когда выбран пресет. В режиме, где
    /// не запускается ничего, список пуст, и это верно: запускать нечего.
    /// </remarks>
    private IReadOnlyList<EngineRow> Planned(AppSettings settings, string detail, string colourKey)
    {
        var rows = new List<EngineRow>();
        var colour = (Brush)FindResource(colourKey);

        if (settings.NeedsProxy)
            rows.Add(new EngineRow("sing-box", detail, string.Empty, colour));

        if (settings.NeedsDesync)
            rows.Add(new EngineRow("winws2", detail, string.Empty, colour));

        return rows;
    }

    private EngineRow Row(ServiceState service)
    {
        // Слова те же, что в консоли. Degraded — это «процесс жив, а проверка
        // не проходит»: самый коварный случай, и называть его «работает»
        // нельзя, иначе окно будет уверять в исправности молчащей трубы.
        // Слова — общие с треем и nz status (EngineHealth.Status): причина
        // надзора и время начала, а не «запущен, но не отвечает» (26.09).
        var key = service.Health switch
        {
            ServiceHealth.Healthy => "Accent",
            ServiceHealth.Degraded => "Warn",
            ServiceHealth.Dead or ServiceHealth.Faulted => "Danger",
            _ => "Faint",
        };

        // Причина — только у того, что не работает: у работающего Status
        // говорит «работает, процесс N», а номер и так стоит строкой ниже.
        // У работающего — только примечание надзора («временная замена…»).
        var detail = service.Health == ServiceHealth.Healthy
            ? service.Remark ?? string.Empty
            : EngineHealth.Status(service);

        return new EngineRow(service.Name, EngineHealth.Word(service.Health), detail, (Brush)FindResource(key), service.ProcessId);
    }

    /// <summary>
    /// То, что стоит знать до запуска.
    /// </summary>
    /// <remarks>
    /// Измерено на живых машинах и приходит молча: защитник со своим сетевым
    /// фильтром обесценивает десинк, ничего об этом не сообщая. Что Kaspersky
    /// вернул hosts к своему умолчанию, с 24.10 говорит только «Диагностика»
    /// (а ещё «Файл hosts» и проверка блокировок), не «Главная» — владелец:
    /// «предупреждение … прям на главной, а должно быть в диагностике».
    /// </remarks>
    private IReadOnlyList<EngineRow>? _engines;

    /// <summary>Движок не работает, как надо: упал, поднимается, проверка не проходит.</summary>
    private bool _enginesTrouble;
    private IReadOnlyList<WarningRow>? _warnings;
    private DateTimeOffset _warningsAt = DateTimeOffset.MinValue;
    private bool _warningsBusy;

    /// <summary>Сколько держать проверку антивирусов, прежде чем повторить.</summary>
    private static readonly TimeSpan WarningsInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Карточки движков — только если что-то в них изменилось.
    /// </summary>
    /// <remarks>
    /// Прежде список собирался заново каждые две секунды: новый ItemsSource —
    /// это новые элементы со всеми шаблонами, и окно дёргалось раз в два
    /// тика даже на RTX 4060 (владелец, 24.09: «интерфейс подвисать может»).
    /// Строки — записи, и сравниваются по значению: одинаковые не трогаем.
    /// </remarks>
    private void SetEngines(IReadOnlyList<EngineRow> rows)
    {
        if (_engines is not null && _engines.SequenceEqual(rows))
            return;

        _engines = rows;
        ShowEngines(rows);
    }

    private void ShowEngines(IReadOnlyList<EngineRow> rows)
    {
        // Движки — в карточке «Режим» (04.10), оба при любом режиме (владелец:
        // «пусть эти движки будут и в десинке, и в туннеле»): не нужный режиму
        // скажет «выключен», как в трее, а высота карточки от режима не зависит.
        ShowEngine(rows.FirstOrDefault(r => r.Name == "winws2"), DesyncDot, DesyncState, DesyncPid, _desyncIdle);
        ShowEngine(rows.FirstOrDefault(r => r.Name == "sing-box"), TunnelDot, TunnelState, TunnelPid);
        ShowModeLine();
    }

    /// <summary>
    /// Строка под режимами: оба движка всегда, оговорка о режиме — когда есть.
    /// </summary>
    /// <remarks>
    /// 11.11 в простом виде движки показывались, только когда один не в порядке;
    /// 12.11 владелец попросил их постоянными — двумя плашками во всю строку.
    /// Оговорка — режим не выбран или «без исключений».
    /// </remarks>
    private void ShowModeLine()
    {
        bool said = ModeText.Text.Length > 0;
        ModeText.Visibility = said ? Visibility.Visible : Visibility.Collapsed;
        DesyncChip.Visibility = TunnelChip.Visibility = Visibility.Visible;
        ModeLine.Visibility = Visibility.Visible;

        // В простом виде оговорка — строкой над плашками, с отступом до них.
        ModeText.Margin = _advanced ? new Thickness(0, 0, 14, 0) : new Thickness(0, 0, 0, said ? 8 : 0);
    }

    /// <summary>
    /// Десинк включён, а не поднимается: всё забрал туннель.
    /// </summary>
    /// <remarks>
    /// Так при «Игнорировать исключения» (<see cref="EngineChoice.DesyncRuns"/>).
    /// 19.10 в чате у XesSarix «Гибрид» был выбран, а десинк стоял «выключен»,
    /// и он спросил почему: причина была только красной строкой под карточкой.
    /// </remarks>
    private string? _desyncIdle;

    /// <summary>Строка состояния движка в его карточке.</summary>
    /// <remarks>
    /// Нет строки — движок в этот запуск не поднимали: выключен или нет
    /// подписки. «Остановлен» здесь соврало бы, что он был.
    /// </remarks>
    /// <param name="idle">Почему не поднимали включённый; <c>null</c> — выключен.</param>
    private void ShowEngine(EngineRow? row, System.Windows.Shapes.Ellipse dot, TextBlock state, TextBlock pid, string? idle = null)
    {
        if (row is null)
        {
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Faint");
            state.Text = idle is null ? "выключен" : "не нужен";
            pid.Text = idle is null ? string.Empty : "всё через VPN";
            pid.ToolTip = idle;
            return;
        }

        dot.Fill = row.Color;
        state.Text = row.Word;

        // Причина надзора у движка не в порядке; в строке места мало — целиком
        // в подсказке. Номер процесса — чтобы найти движок в диспетчере задач
        // (владелец, 26.09) — только в подробном виде: в простом строка видна
        // лишь при беде, и номер в ней лишний.
        pid.Text = row.Detail.Length > 0
            ? row.Detail
            : _advanced && row.ProcessId is { } id ? $"№{id}" : string.Empty;
        pid.ToolTip = row.Detail.Length > 0
            ? row.Detail
            : _advanced && row.ProcessId is { } shown ? $"Процесс {shown} — так его ищут в диспетчере задач" : null;
    }

    /// <summary>
    /// Предупреждения — в фоне и раз в полминуты, а не в потоке окна каждые две секунды.
    /// </summary>
    /// <remarks>
    /// Проверка антивирусов перебирает все процессы системы. За две секунды
    /// это не меняется, а делалось в потоке окна на каждом тике.
    /// </remarks>
    private void ShowWarnings(AppSettings settings)
    {
        if (_warningsBusy || DateTimeOffset.Now - _warningsAt < WarningsInterval)
            return;

        _warningsBusy = true;
        bool needsDesync = settings.NeedsDesync;

        _ = Task.Run(() => CollectWarnings(needsDesync)).ContinueWith(task =>
        {
            _warningsBusy = false;
            _warningsAt = DateTimeOffset.Now;

            if (task.IsCompletedSuccessfully)
                SetWarnings(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Предупреждения о защитниках — из фоновой проверки раз в полминуты.</summary>
    private IReadOnlyList<WarningRow> _guardWarnings = [];

    /// <summary>Карточка о резолвере движка — из состояния надзора, каждые две секунды.</summary>
    private WarningRow? _dnsWarning;

    private void SetWarnings(IReadOnlyList<WarningRow> rows)
    {
        _guardWarnings = rows;
        ShowAllWarnings();
    }

    /// <summary>DNS роутера недоступен — работает запасной DoH.</summary>
    private WarningRow? _routerDnsWarning;

    /// <summary>
    /// DNS роутера выбран, а не годится — карточкой предупреждений.
    /// </summary>
    /// <remarks>
    /// До 11.11 это было словами в строке «DNS» карточки параметров; строка
    /// DNS ушла с «Главной» (что выбрано — вкладка DNS), а беда осталась на виду.
    /// </remarks>
    private void SetRouterDnsWarning(AppSettings settings)
    {
        WarningRow? row = !_advanced && settings.UseRouterDns && RouterDnsConnection.ConfigWarning(settings) is not null
            ? new WarningRow("DNS роутера недоступен",
                $"Имена спрашиваются у запасного DoH — {settings.DnsServer}. Проверить роутер — вкладка DNS.")
            : null;

        if (row == _routerDnsWarning)
            return;

        _routerDnsWarning = row;
        ShowAllWarnings();
    }

    private void SetDnsWarning(WarningRow? row)
    {
        if (row == _dnsWarning)
            return;

        _dnsWarning = row;
        ShowAllWarnings();
    }

    /// <summary>Резолвер — первым: без имён не работает ничего, и это важнее защитника.</summary>
    private void ShowAllWarnings()
    {
        var rows = new List<WarningRow>();

        if (_dnsWarning is { } dns)
            rows.Add(dns);

        if (_routerDnsWarning is { } router)
            rows.Add(router);

        rows.AddRange(_guardWarnings);

        if (_warnings is not null && _warnings.SequenceEqual(rows))
            return;

        _warnings = rows;
        Warnings.ItemsSource = rows;
    }

    /// <summary>Карточка о резолвере (SingBoxService, DnsLadder): что с ним и что делать.</summary>
    private static WarningRow DnsWarning(ServiceState service)
    {
        var notice = service.DnsNotice!;

        return service.DnsDown
            ? new WarningRow("Резолвер движка не отвечает",
                char.ToUpper(notice[0]) + notice[1..] + ". Выберите другой резолвер на вкладке DNS и перезапустите движки.")
            : new WarningRow("Резолвер движка не отвечает — работает запасной путь",
                char.ToUpper(notice[0]) + notice[1..] + ". Сайты открываются; вернётся сам, как только резолвер ответит. "
                + "Чтобы не зависеть от него, выберите другой на вкладке DNS.");
    }

    private SystemDnsVerdict? _systemDns;
    private DateTimeOffset _systemDnsAt;
    private DateTimeOffset? _systemDnsRun;
    private bool _systemDnsBusy;

    /// <summary>
    /// Карточка о подмене DNS провайдера — в «Десинке», где имена спрашивает
    /// Windows (<see cref="SystemDnsCheck"/>); проверка в фоне при каждом запуске
    /// движков и раз в 10 минут, три пакета UDP.
    /// </summary>
    private WarningRow? SystemDnsWarning(AppSettings settings, SupervisorState state)
    {
        if (!SystemDnsCheck.Applies(settings))
            return null;

        bool stale = _systemDnsRun != state.StartedAt || DateTimeOffset.Now - _systemDnsAt > TimeSpan.FromMinutes(10);

        if (stale && !_systemDnsBusy)
            _ = CheckSystemDnsAsync(state.StartedAt);

        return _systemDns is { Kind: SystemDnsKind.Forged } forged && _systemDnsRun == state.StartedAt
            ? new WarningRow("DNS провайдера подменяет ответы", SystemDnsCheck.Advice(forged))
            : null;
    }

    private async Task CheckSystemDnsAsync(DateTimeOffset? run)
    {
        _systemDnsBusy = true;

        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            _systemDns = await Task.Run(() => SystemDnsCheck.CheckAsync(limit.Token));
        }
        catch (Exception)
        {
            _systemDns = null;
        }
        finally
        {
            _systemDnsAt = DateTimeOffset.Now;
            _systemDnsRun = run;
            _systemDnsBusy = false;
        }
    }

    private static IReadOnlyList<WarningRow> CollectWarnings(bool needsDesync)
    {
        var rows = new List<WarningRow>();

        var guards = SecuritySoftware.Running();

        if (guards.Count > 0 && needsDesync)
        {
            rows.Add(new WarningRow(
                $"Работает {string.Join(", ", guards.Select(g => g.Name))}",
                "Его сетевой фильтр встаёт на тот же слой, что и наш, проверка защищённых "
                + "соединений переустанавливает TLS своим клиентом, а WinDivert он помечает "
                + "как RiskTool и может увезти в карантин. Мы этого отсюда не видим; если "
                + "обход не помогает без внятной причины — отключите защиту на десять минут "
                + "и повторите."));
        }

        return rows;
    }

    /// <summary>Причины упавших движков, по строке на движок: «Десинк: …».</summary>
    private static string Failures(IEnumerable<ServiceState> services) => string.Join("\n", services
        .Where(s => s.Health is ServiceHealth.Dead or ServiceHealth.Faulted && s.LastError is { Length: > 0 })
        .Select(s => $"{EngineTitle(s.Name)}: {s.LastError}"));

    private static string EngineTitle(string name) => name switch
    {
        "winws2" => "Десинк",
        "sing-box" => "Туннель",
        _ => name,
    };

    private void ShowProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    private static string Ago(DateTimeOffset since)
    {
        var passed = DateTimeOffset.Now - since;

        return passed switch
        {
            { TotalMinutes: < 1 } => "только что",
            { TotalHours: < 1 } => $"{(int)passed.TotalMinutes} мин назад",
            { TotalDays: < 1 } => $"{(int)passed.TotalHours} ч назад",
            _ => $"{(int)passed.TotalDays} дн назад",
        };
    }

    /// <summary>
    /// Пять режимов с ценой каждого.
    /// </summary>
    /// <remarks>
    /// Цена названа в самом описании, а не выясняется опытом: «всё через VPN
    /// без исключений» ломает отечественные сервисы, и узнавать об этом
    /// по неработающим госуслугам человек не должен.
    /// </remarks>
    private void ShowModes(AppSettings settings)
    {
        var engines = settings.Engines;
        var mode = WorkModes.Of(engines);

        ModeDesyncName.Text = TileDesyncName.Text = WorkModes.Name(WorkMode.Desync);
        ModeTunnelName.Text = TileTunnelName.Text = WorkModes.Name(WorkMode.Tunnel);
        ModeRouteName.Text = TileRouteName.Text = WorkModes.Name(WorkMode.Route);

        // Пояснение — выбранного режима. «Без исключений» — настройка внутри
        // «Туннеля»: включённая, она меняет смысл режима, и молчать о ней нельзя.
        // У «Гибрида» она отменяет само пояснение: маршруты не решают ничего,
        // всё уходит в VPN, а десинк не поднимается (EngineChoice.DesyncRuns).
        // Пояснение выбранного режима — в подробном виде, как до 11.11. В простом
        // обычное пояснение (WorkModes.Explain) — только в «i» у кнопки режима,
        // а здесь лишь то, что меняет его смысл сейчас. «Без исключений» —
        // настройка внутри «Туннеля»: у «Гибрида» она отменяет само пояснение.
        ModeText.Text = mode is not { } chosen
            ? "Режим не выбран — движки не поднимутся."
            : engines.IgnoreExclusions && chosen == WorkMode.Route
                ? "Сейчас без исключений: всё идёт через VPN, маршруты и десинк не действуют."
                : _advanced
                    ? WorkModes.Explain(chosen)
                        + (chosen == WorkMode.Tunnel && engines.IgnoreExclusions
                            ? " Сейчас без исключений: в VPN идёт и «напрямую»."
                            : string.Empty)
                    : engines.IgnoreExclusions && chosen == WorkMode.Tunnel
                        ? "Сейчас без исключений: в VPN идёт всё, и «напрямую» тоже."
                        : string.Empty;
        ModeText.ToolTip = ModeText.Text.Length > 0 ? ModeText.Text : null;

        _desyncIdle = engines.Desync && !engines.DesyncRuns
            ? "Включено «Игнорировать исключения» в настройках туннеля: всё идёт через VPN, "
                + "и десинку чинить нечего. Выключите его — заработают маршруты и десинк."
            : null;

        if (_engines is { } shown)
            ShowEngines(shown);
        else
            ShowModeLine();

        // «Ничего не поднято» режимом не считается — ни один сегмент не выбран.
        ModeDesync.IsChecked = TileDesync.IsChecked = mode == WorkMode.Desync;
        ModeTunnel.IsChecked = TileTunnel.IsChecked = mode == WorkMode.Tunnel;
        ModeRoute.IsChecked = TileRoute.IsChecked = mode == WorkMode.Route;

        // Режиму с туннелем без выхода везти некуда: «Туннель» не поднимет
        // ничего, «Гибрид» сработает как «Десинк». Сказать об этом здесь,
        // у выбора, а не узнавать по неработающему VPN.
        var complaint = engines.Complaint
            ?? (engines.Tunnel && !settings.HasTunnelExit
                ? (engines.Desync
                    ? $"VPN не задан — «{WorkModes.RouteName}» работает как «{WorkModes.Name(WorkMode.Desync)}». Подписка или WARP — на вкладке VPN."
                    : $"Для «{WorkModes.Name(WorkMode.Tunnel)}» нужна подписка или WARP — вкладка VPN.")
                : null);

        EnginesLine.Text = complaint ?? string.Empty;
        EnginesLine.Visibility = complaint is null ? Visibility.Collapsed : Visibility.Visible;

        if (_advanced && Autostart.Known is { Stale: false })
            AutostartValue.Text = Autostart.Raises(settings, atLogon: true);
    }

    // --- Вид «Главной»: простой или подробный (11.11) ---------------------------

    /// <summary>
    /// Показывает «Главную» в выбранном виде.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Простой — состояние, пресет, сервер, режим и нагрузка; прочее встаёт,
    /// только когда есть что сказать. Подробный — как было до 11.11: параметры
    /// подключения, движки с номерами и пояснение режима всегда, автозапуск,
    /// обновление, быстрые действия и полная строка внизу. Ссылки в шапке —
    /// в обоих видах.
    /// </para>
    /// <para>
    /// Раздел один, а не два: состояние, волна, кнопка и режимы общие, вид
    /// меняет видимость частей и то, что из них заполняется. Переключение
    /// перестраивает раздел сразу, без перезапуска.
    /// </para>
    /// </remarks>
    internal void ApplyView(bool advanced, AppSettings settings)
    {
        _advanced = advanced;

        ViewSimple.IsChecked = !advanced;
        ViewAdvanced.IsChecked = advanced;

        var shown = advanced ? Visibility.Visible : Visibility.Collapsed;

        Actions.Visibility = shown;
        AutostartCard.Visibility = shown;

        // Обновление: в подробном — рядом с автозапуском, в простом — во всю
        // ширину и только на время установки (UpdatePanel.ShowView).
        SettingsGap.Width = new GridLength(advanced ? 14 : 0);
        Grid.SetColumn(Updates, advanced ? 2 : 0);
        Grid.SetColumnSpan(Updates, advanced ? 1 : 3);
        Updates.ShowView(advanced);

        if (advanced)
            ShowAutostart();

        ShowLayout(advanced);
        ShowFooterRow();
        ShowCards(settings);
        ShowModes(settings);

        if (_engines is { } rows)
            ShowEngines(rows);

        // Карточка «DNS роутера недоступен» — только простому виду: в подробном
        // это сказано строкой DNS в параметрах подключения.
        SetRouterDnsWarning(settings);
    }

    /// <summary>
    /// Порядок и размеры частей по виду.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Простой вид — по макету владельца (11.11): состояние, «Сайт не
    /// открывается?», режим плитками, пресет и сервер, внизу нагрузка и отчёт.
    /// Подробный — как до 11.11: пресет и параметры сразу под состоянием,
    /// режим сегментами, автозапуск с обновлением и быстрые действия.
    /// </para>
    /// <para>
    /// Карточка состояния в простом виде — главная, но постоянной высоты
    /// (<see cref="StateTall"/>, 12.11). Первый вид 11.11 растягивал её по
    /// остатку страницы (168–284 точки), и владелец увидел, что она скачет:
    /// любая строка ниже — ошибка под полем сайта, итог отчёта, предупреждение —
    /// меняла её высоту, а на развёрнутом окне под волной пустовала треть.
    /// Нижняя строка (нагрузка и «Собрать отчёт») стоит сразу под карточками,
    /// а не прижата к низу страницы: оторванная, она висела отдельно.
    /// </para>
    /// </remarks>
    private void ShowLayout(bool advanced)
    {
        var simple = advanced ? Visibility.Collapsed : Visibility.Visible;

        SiteCard.Visibility = simple;
        Grid.SetRow(CardsRow, advanced ? 6 : 13);
        Grid.SetRow(SettingsRow, advanced ? 13 : 15);

        // Состояние — постоянной высоты в обоих видах (см. выше).
        StateGrid.Height = advanced ? 128 : StateHeight(ActualHeight);

        // Над нижней строкой: в подробном она прижата к низу, как была; в простом —
        // сразу под карточками тем же шагом, что между ними (зазор перед строкой
        // обновления уже его даёт), а остаток высоты уходит под неё.
        var gap = Page.RowDefinitions[16];
        gap.Height = new GridLength(1, GridUnitType.Star);
        gap.MaxHeight = advanced ? double.PositiveInfinity : 0;
        StateText.Margin = advanced ? new Thickness(24, 16, 0, 0) : new Thickness(28, 24, 0, 0);
        StateText.MaxWidth = advanced ? 340 : 420;
        StateLine.FontSize = advanced ? 21 : 36;
        StateLine.MaxHeight = advanced ? 58 : 98;
        StateHint.FontSize = advanced ? 13.5 : 15;
        StateHint.Margin = new Thickness(0, advanced ? 4 : 6, 0, 0);

        // Движки: подробному — справа от пояснения одной строкой 34; простому —
        // двумя плашками пополам во всю ширину, пояснение (оговорка) — над ними.
        ModeLine.Height = advanced ? 34 : double.NaN;
        Grid.SetRow(EngineChips, advanced ? 0 : 1);
        Grid.SetColumn(EngineChips, advanced ? 1 : 0);
        Grid.SetColumnSpan(EngineChips, advanced ? 1 : 2);
        Grid.SetColumnSpan(ModeText, advanced ? 1 : 2);
        EngineChips.HorizontalAlignment = advanced ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ModeLine.Margin = advanced ? new Thickness(4, 10, 0, 0) : new Thickness(0, 12, 0, 0);
        DesyncChipColumn.Width = TunnelChipColumn.Width = advanced ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        EngineChipsGap.Width = new GridLength(advanced ? 16 : 12);

        foreach (var chip in new[] { DesyncChip, TunnelChip })
        {
            if (advanced)
                chip.ClearValue(Border.BackgroundProperty);
            else
                chip.SetResourceReference(Border.BackgroundProperty, "Raised");

            chip.Padding = advanced ? new Thickness(0) : new Thickness(14, 6, 6, 6);
        }

        // Режим: сегменты — подробному, плитки — простому.
        ModeCaption.Text = advanced ? "Режим работы NZ" : "Режим работы";
        ModeSegments.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        ModeTiles.Visibility = simple;

        // Пресет: подпись и описание — только подробному (решение владельца 11.11).
        PresetCaption.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        PresetRow.Margin = new Thickness(0, advanced ? 12 : 0, 0, 0);
        PresetBody.VerticalAlignment = advanced ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        PresetMenu.VerticalAlignment = advanced ? VerticalAlignment.Top : VerticalAlignment.Center;
    }

    /// <summary>
    /// Высота карточки состояния простого вида: надпись, волна под ней и кнопка
    /// со счётчиками, без пустой полосы под волной (12.11, снимок владельца —
    /// карточка должна кончаться сразу под пунктиром).
    /// </summary>
    internal const double StateTall = 200;

    /// <summary>Она же в невысоком окне — как было до 12.11 на его минимуме.</summary>
    internal const double StateShort = 168;

    /// <summary>
    /// Высота карточки состояния по высоте раздела, а не по содержимому.
    /// </summary>
    /// <remarks>
    /// С постоянными движками (12.11) простая «Главная» в неразвёрнутом окне
    /// владельца не влезала: страница 677 при видимых 651 (HomeFitTests, Manrope).
    /// Высота зависит только от окна: от строк ниже — ошибки под полем, итога
    /// отчёта, предупреждения — она не меняется, и карточка не скачет; лишнее
    /// уходит в прокрутку. Развёрнутое окно владельца — раздел 794, неразвёрнутое — 674.
    /// </remarks>
    internal static double StateHeight(double sectionHeight) => sectionHeight >= 740 ? StateTall : StateShort;

    // --- «Сайт не открывается?» — простой вид ---------------------------------------

    /// <summary>«Починить» залита, только когда в поле что-то есть: заливка на странице одна.</summary>
    private void OnSiteText(object sender, TextChangedEventArgs e)
    {
        SiteFixButton.SetResourceReference(StyleProperty, SiteBox.Text.Trim().Length > 0 ? "Primary" : "Ghost");
        SiteError.Visibility = Visibility.Collapsed;
    }

    private void OnSiteKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            FixSite();
    }

    private void OnSiteFix(object sender, RoutedEventArgs e) => FixSite();

    /// <summary>
    /// «Починить» — автоподбор маршрута, тот же, что «Сайт не открывается?» в «Ещё».
    /// </summary>
    /// <remarks>
    /// 11.11 кнопка только открывала «Маршруты» на части сайта (нет части —
    /// заводила его своим доменом «напрямую»); 12.11 владелец попросил, чтобы
    /// она запускала автоподбор (<see cref="SiteFix"/>): путь пробуется замером,
    /// первый открывший записывается правилом сайта, а «Показать в «Маршрутах»»
    /// в итоге ведёт на его часть. Имя — тем же разбором (<see cref="SiteFixer.HostOf"/>):
    /// не сайт — строка под полем, а не подбор вслепую.
    /// </remarks>
    private async void FixSite()
    {
        if (SiteFixer.HostOf(SiteBox.Text) is null)
        {
            SiteError.Text = SiteBox.Text.Trim().Length == 0
                ? "Впишите сайт, например aternos.org."
                : "Это не похоже на адрес сайта. Нужно что-то вроде aternos.org.";
            SiteError.Visibility = Visibility.Visible;
            return;
        }

        SiteError.Visibility = Visibility.Collapsed;
        _siteHost = await SiteFix.RunAsync(SiteBox.Text, SiteProgress, SiteFixButton, this);
    }

    /// <summary>Имя последнего подбора на «Главной» — для «Показать в «Маршрутах»».</summary>
    private string? _siteHost;

    private void OnSiteReveal(object sender, RoutedEventArgs e) => SiteFix.ShowRoute(_siteHost, this);

    // --- Отчёт для разбора — простой вид, тот же, что в «Диагностике» -----------------

    /// <summary>
    /// «Обновление» простого вида: спросить GitHub и открыть окно обновления.
    /// </summary>
    /// <remarks>
    /// То же, что «Проверить» в карточке подробного вида (<see cref="UpdatePanel.AskAsync"/>):
    /// окно открывается и при последней версии — с её чейнджлогом и погашенной
    /// «Обновить». GitHub не ответил — окна нет, итог строкой рядом.
    /// </remarks>
    private async void OnHomeUpdate(object sender, RoutedEventArgs e)
    {
        HomeUpdateButton.IsEnabled = false;
        ReportNote.Text = "Спрашиваю GitHub…";
        ReportNote.ToolTip = null;
        ReportNote.Visibility = Visibility.Visible;

        try
        {
            var (found, text, _) = await UpdatePanel.AskAsync(CancellationToken.None);

            ReportNote.Text = text;
            ReportNote.ToolTip = text;

            if (found is not null)
            {
                ReportNote.Visibility = Visibility.Collapsed;
                (Window.GetWindow(this) as MainWindow)?.ShowUpdateWindow(found);
            }
        }
        finally
        {
            HomeUpdateButton.IsEnabled = true;
        }
    }

    private async void OnReport(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        ReportNote.Text = "Собираю отчёт…";
        ReportNote.ToolTip = null;
        ReportNote.Visibility = Visibility.Visible;

        var said = await ReportMaker.MakeAsync();

        ReportNote.Text = said;
        ReportNote.ToolTip = said;
        ReportButton.IsEnabled = true;
    }

    /// <summary>Выбран вид «Главной» — в настройки и сразу на экран.</summary>
    private void OnHomeView(object sender, RoutedEventArgs e)
    {
        bool advanced = ViewAdvanced.IsChecked == true;

        if (advanced == _advanced)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { HomeAdvanced = advanced };
            settings.Save(AppSettings.DefaultPath);

            ApplyView(advanced, settings);
            Update();
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать выбор: " + ex.GetBaseException().Message);
            ViewSimple.IsChecked = !_advanced;
            ViewAdvanced.IsChecked = _advanced;
        }
    }

    // --- Автозапуск — карточка подробного вида; логика общая с «Ещё» (Autostart) ---

    private async void ShowAutostart()
    {
        if (Autostart.Known is { } known)
        {
            ApplyAutostart(known);
        }
        else
        {
            AutostartValue.Text = Autostart.Asking;
            AutostartSwitch.IsEnabled = false;
        }

        try
        {
            ApplyAutostart(await Autostart.QueryAsync());
        }
        catch (Exception ex)
        {
            AutostartValue.Text = "Задача планировщика не читается.";
            AutostartSwitch.IsEnabled = false;
            ShowProblem("Планировщик не отвечает: " + ex.GetBaseException().Message);
        }
    }

    private void ApplyAutostart((bool Installed, bool Stale) state)
    {
        AutostartSwitch.IsChecked = state.Installed;
        AutostartSwitch.IsEnabled = true;
        AutostartValue.Text = state.Stale
            ? Autostart.StaleText
            : Autostart.Raises(AppSettings.Load(AppSettings.DefaultPath), atLogon: true);
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        if (Autostart.Toggle() is { } failure)
            ShowProblem(failure);

        ShowAutostart();
    }

    /// <summary>
    /// Переключает движок.
    /// </summary>
    /// <remarks>
    /// Через <see cref="AppSettings.With(EngineChoice)"/>: тот пишет заодно
    /// и выведенный режим, на языке которого говорят конфиг движка, отчёты
    /// и консоль. Оставленный отставшим, режим развёл бы показания.
    /// </remarks>
    private void Choose(Func<EngineChoice, EngineChoice> change)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings.With(change(settings.Engines));

            next.Save(AppSettings.DefaultPath);

            ShowModes(next);
            Update();

            this.Offer("Режим: " + next.Engines.Describe());
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Выбран режим — выключатели под него (Rules.WorkModes) и предложение перезапустить.</summary>
    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<WorkMode>(tag, out var mode))
            Choose(c => WorkModes.Apply(c, mode));
    }

    /// <summary>
    /// Собирает конфиг и поднимает движки.
    /// </summary>
    /// <remarks>
    /// Сборка идёт при каждом запуске, как в меню консоли. Без неё окно
    /// поднимало движки с тем конфигом, что лежал на диске: смена сервера,
    /// правки маршрутов и переключение режима показывались новыми, а до
    /// туннеля не доходили, пока конфиг не соберут отдельно.
    /// </remarks>
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StateLine.Text = "Собираю конфиг…";
        StateHint.Text = "Читаю подписку и правила.";

        var outcome = await EngineControl.StartAsync("кнопка «Запустить» на «Главной»", CancellationToken.None);

        StartButton.IsEnabled = true;

        if (!outcome.Ok)
        {
            ShowProblem(outcome.Message);
            Update();
            return;
        }

        // Прежде кнопки просто гасли на три секунды. Этого хватало, пока
        // запуск был мгновенным; с проверкой прохода трафика он занимает
        // десятки секунд, и кнопки оживали посреди подъёма, показывая
        // «остановлено» у ещё запускающегося движка.
        BeginStarting();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        EndStarting();

        StopButton.IsEnabled = false;
        StateLine.Text = "Останавливаю…";

        await EngineControl.StopAsync("кнопка «Остановить» на «Главной»", CancellationToken.None);

        StopButton.IsEnabled = true;
        Update();
    }


    /// <summary>
    /// «На всю ширину» (Оформление, 01.10; с 20.10 — у каждой темы своё): без предела у содержимого и без карточки с картинкой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Фон тогда — как у прочих вкладок: своя картинка «Главной» прячется,
    /// подложка становится прозрачной (Chrome), и видна картинка окна под
    /// затемнением темы. Владелец 01.10: растянули — «обои темы не должны
    /// сдвигаться вправо».
    /// </para>
    /// <para>
    /// Своя картинка пересчитывается только по размеру своего слоя, а он
    /// при смене колонок не меняется: стоило лишь обнулить колонку, и арт
    /// оставался на месте, рассчитанном под неё, — сдвинутым вправо.
    /// </para>
    /// </remarks>
    private void ShowWidth(AppSettings settings)
    {
        // У каждой темы своё (владелец 20.10): выбор человека в «Оформлении»,
        // иначе выбор самой темы — темы без картинки и «Рыбка» (ролик 132×74
        // в карточке — размытое пятно) на всю ширину, прочие с карточкой.
        if (settings.HomeWide(Themes.Current, Themes.WideHome))
        {
            ContentColumn.MaxWidth = double.PositiveInfinity;
            ArtColumn.Width = new GridLength(0);
            ArtLayer.Visibility = Visibility.Collapsed;
            ArtTile.Visibility = Visibility.Collapsed;
            SetResourceReference(BackgroundProperty, "Chrome");
        }
        else
        {
            ContentColumn.MaxWidth = 940;
            ArtColumn.Width = new GridLength(1, GridUnitType.Star);
            ArtLayer.Visibility = Visibility.Visible;

            // Спрятанная во всю ширину рамка снова раскладывается, но не видна,
            // пока PlaceArt не решит, есть ли ей место и картинка.
            if (ArtTile.Visibility == Visibility.Collapsed)
                ArtTile.Visibility = Visibility.Hidden;
            SetResourceReference(BackgroundProperty, "Backdrop");

            // Слой мог прятаться — разложить заново, когда станет известен размер.
            Dispatcher.BeginInvoke(PlaceArt, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
