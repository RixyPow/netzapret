using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Рецепт в каталоге.
/// </summary>
/// <remarks>
/// Запись, а не класс с уведомлениями: каталог перестраивается целиком —
/// его содержимое меняется только вместе с выбранным пресетом, а это
/// и так перезагрузка вкладки.
/// </remarks>
public sealed record CatalogRow
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string What { get; init; }

    public required string Note { get; init; }

    public required string Detail { get; init; }

    /// <summary>Чего не хватает; пусто — рецепт годен.</summary>
    public required string Complaint { get; init; }

    /// <summary>Модуль, который предлагается подключить; <c>null</c> — нечего.</summary>
    public required string? Missing { get; init; }

    public Visibility NoteShown =>
        Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ComplaintShown =>
        Complaint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Кнопка «Подключить модуль».
    /// </summary>
    /// <remarks>
    /// Только когда модуль есть на диске. Если его нет и там, подключать
    /// нечего, и кнопка обещала бы починку, которой не будет: пресету
    /// дописали бы строку, а winws2 всё равно не поднялся бы.
    /// </remarks>
    public Visibility FixShown =>
        Missing is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Рамка: недоступный рецепт отмечен и ею, не только словами.</summary>
    public Brush Edge => (Brush)Application.Current.FindResource(
        Complaint.Length > 0 ? "Warn" : "Border");
}

/// <summary>Пресет в списке выбора.</summary>
public sealed record PresetRow(string Name, string Version, string Fake) : INotifyPropertyChanged
{
    private string _detail = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Имя файла — то, чем строка отличается от соседней.
    /// </summary>
    /// <remarks>
    /// Порядок раскладывается по нему, а не по названию пресета: три пресета
    /// Zapret объявляют себя «Universal V5», и раскладка по названию роняла
    /// весь список жалобой на повторный ключ.
    /// </remarks>
    public required string File { get; init; }

    /// <summary>
    /// Подпись строки; меняется на месте, когда досчитано покрытие.
    /// </summary>
    /// <remarks>
    /// С уведомлением, а не пересборкой списка (01.10): прежде на каждый
    /// досчитанный пресет оба списка пересоздавались целиком, шестнадцать раз
    /// подряд, — поток окна стоял по 330–730 мс (журнал сторожа), и проявление
    /// раздела шло рывками.
    /// </remarks>
    public required string Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public bool Chosen { get; set; }

    /// <summary>
    /// Наш пресет, а не доставшийся от Zapret.
    /// </summary>
    /// <remarks>
    /// По имени, а не по списку внутри программы: список пришлось бы править
    /// при каждом новом пресете, и забытая строка молча переселила бы наш
    /// пресет к чужим. Universal ведём мы — это и есть признак.
    /// </remarks>
    public bool Official =>
        Name.StartsWith("Universal", StringComparison.OrdinalIgnoreCase);

    public Brush Edge =>
        (Brush)Application.Current.FindResource(Chosen ? "Accent" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ApplyShown => Chosen ? Visibility.Collapsed : Visibility.Visible;

    public Visibility VersionShown => Version.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FakeShown => Fake.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Выбор пресета десинка.
/// </summary>
/// <remarks>
/// <para>
/// Пресеты — наши, из папки <c>presets</c> рядом с программой. Читаются тем же
/// <see cref="PresetReader"/>, что и в консоли, поэтому список здесь и в меню
/// один и тот же.
/// </para>
/// <para>
/// Выбор пишется в настройки и применяется перезапуском движков. Сами
/// не перезапускаем: winws2 несёт весь трафик машины, и ронять его в ответ
/// на нажатие в списке — не та цена, на которую человек соглашался, выбирая
/// пресет.
/// </para>
/// </remarks>
public partial class DesyncView : UserControl
{
    private CancellationTokenSource? _counting;

    /// <summary>Показанные строки в нынешнем порядке.</summary>
    private IReadOnlyList<PresetRow> _rows = [];

    public DesyncView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _counting?.Cancel();
    }

    private void OnGameFilter(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings with { GameFilter = !settings.GameFilter };

            next.Save(AppSettings.DefaultPath);
            GameFilterSwitch.IsChecked = next.GameFilter;

            // Game filter расширяет перехват — подсказка перехвата о нём говорит.
            ShowCapture(next);

            Status.Text = next.GameFilter
                ? "Game filter включён. Проверьте игру на клиенте, открытом после перезапуска движков."
                : "Game filter выключен.";

            this.Offer(next.GameFilter ? "Game filter включён" : "Game filter выключен");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        GameFilterSwitch.IsChecked = settings.GameFilter;
        ShowChosen(settings);
        ShowEngine();

        // До списка пресетов, а не после: список умеет уйти по короткому
        // пути — пустая папка, нечитаемый файл, — и каталог, собираемый
        // следом, в этих случаях не собирался бы вовсе. А он осмыслен
        // и без пресетов: это перечень того, что бывает.
        ShowCatalog(settings);

        try
        {
            var files = ZapretPaths.PresetFiles;

            if (files.Count == 0)
            {
                Status.Text = $"В папке {ZapretPaths.PresetDirectory} нет ни одного пресета.";

                _rows = [];
                Redraw();

                return;
            }

            var rows = PresetNewness.Order(
                new PresetReader()
                    .Read(files)
                    .Select(preset => Row(preset, settings.PresetName)),
                row => row.Name,
                row => row.Version);

            _rows = rows;
            Redraw();

            Status.Text = $"Пресетов: {rows.Count}, из них наших {rows.Count(r => r.Official)}. "
                + "Выбор применяется при следующем запуске движков.";

            StartCounting(rows);
        }
        catch (Exception ex)
        {
            Status.Text = "Пресеты не читаются: " + ex.GetBaseException().Message;
        }
    }

    private static PresetRow Row(ZapretPreset preset, string? chosen)
    {
        int active = preset.ActiveSections.Count();
        int pass = preset.Sections.Count(s => s.IsPassThrough);
        int fake = preset.ActiveSections.Count(s => s.UsesFakePackets);

        var file = Path.GetFileName(preset.FilePath);

        // Пресет зовётся по своему файлу, а не по названию внутри него.
        // Так его и находит запуск: ZapretPaths.FindPreset ищет по имени
        // файла. Пока показывалось внутреннее название, два разных файла
        // с одинаковым названием выглядели как одна строка, повторённая
        // дважды, переименование файла ничего не меняло, а сохранённый выбор
        // и поиск при запуске расходились на ровном месте.
        var name = Path.GetFileNameWithoutExtension(file);

        var detail = $"{preset.Sections.Count} {Ending(preset.Sections.Count, "секция", "секции", "секций")}: "
            + $"{active} с десинком, {pass} нетронутыми";

        // Название изнутри показывается, когда расходится с именем файла:
        // его писал автор пресета, и по нему пресет узнают в чужих советах.
        if (!string.IsNullOrWhiteSpace(preset.Name)
            && !string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"внутри «{preset.Name}» · {detail}";
        }

        return new PresetRow(
            name,
            preset.BuiltinVersion ?? string.Empty,

            // Про поддельные пакеты сказано отдельно, потому что именно они
            // ломаются под поднятым TUN, если трафик из туннеля не выведен.
            // Замер 2026-08-23; чистые split и disorder его переживают.
            fake == 0
                ? string.Empty
                : $"{fake} с поддельным пакетом — под туннелем такие секции работают не всегда")
        {
            File = file,
            Detail = detail,
            Chosen = string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Досчитывает, сколько каждый пресет покрывает.
    /// </summary>
    /// <remarks>
    /// В стороне от показа: счёт открывает каждый список, на который ссылается
    /// пресет, а их дюжина на дюжину файлов. Ждать этого, чтобы показать
    /// названия, которые уже разобраны, значит держать раздел пустым секунду
    /// на ровном месте.
    /// </remarks>
    /// <summary>
    /// Покрытие пресетов, уже посчитанное, — до изменения файла.
    /// </summary>
    /// <remarks>
    /// Раздел пересоздаётся при каждом заходе, а счёт стоит дорого: замер 01.10 —
    /// около 50 мс и сотни тысяч строк из списков на пресет, шестнадцать пресетов.
    /// Пересчитывать то, что не менялось, незачем.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, (DateTime Written, int Domains, int Addresses)> Coverage =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пауза перед счётом — чтобы раздел успел проявиться ровно.</summary>
    private static readonly TimeSpan CountingDelay = TimeSpan.FromMilliseconds(400);

    private void StartCounting(IReadOnlyList<PresetRow> rows)
    {
        var root = ZapretPaths.Discover()?.Root;

        if (root is null)
            return;

        _counting?.Cancel();
        _counting = new CancellationTokenSource();

        var token = _counting.Token;
        var pending = new List<(PresetRow Row, string Path, DateTime Written)>();

        foreach (var row in rows)
        {
            // По имени файла, а не по названию пресета: названия у разных
            // файлов совпадают, и поиск по ним считал бы покрытие одного
            // и того же трижды.
            var path = Path.Combine(ZapretPaths.PresetDirectory, row.File);

            DateTime written;

            try
            {
                written = System.IO.File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (Coverage.TryGetValue(path, out var known) && known.Written == written)
                row.Detail += Covered(known.Domains, known.Addresses);
            else
                pending.Add((row, path, written));
        }

        if (pending.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(CountingDelay, token);

            var reader = new PresetReader();

            foreach (var (row, path, written) in pending)
            {
                if (token.IsCancellationRequested)
                    return;

                if (!System.IO.File.Exists(path))
                    continue;

                int domains;
                int addresses;

                try
                {
                    var preset = reader.Load(path);

                    domains = reader.CollectCoveredDomains(preset, root).Count;
                    addresses = reader.CollectCoveredAddresses(preset, root).Count;
                }
                catch (Exception)
                {
                    // Список, на который ссылается пресет, мог не приехать
                    // с установкой Zapret. Пресет от этого не перестаёт быть
                    // выбираемым — просто покрытие неизвестно.
                    continue;
                }

                Coverage[path] = (written, domains, addresses);

                if (token.IsCancellationRequested)
                    return;

                // Без ожидания и без пересборки: строка обновит себя сама.
                _ = Dispatcher.BeginInvoke(() => row.Detail += Covered(domains, addresses));
            }
        }, token);
    }

    private static string Covered(int domains, int addresses) => $" · {domains} доменов, {addresses} подсетей";

    /// <summary>
    /// Раскладывает строки по двум спискам — с поиском, если он задан.
    /// </summary>
    /// <remarks>
    /// Пустая половина скрывается вместе с заголовком: подпись «Пресеты
    /// сообщества» над пустотой обещает то, чего нет. Заголовок наших при
    /// поиске остаётся: поле поиска стоит рядом с ним.
    /// </remarks>
    private void Redraw()
    {
        var needle = Search.Text.Trim();
        bool searching = needle.Length > 0;

        bool Fits(PresetRow row) =>
            !searching || row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

        var ours = _rows.Where(row => row.Official && Fits(row)).ToList();
        var theirs = _rows.Where(row => !row.Official && Fits(row)).ToList();

        Own.ItemsSource = null;
        Own.ItemsSource = ours;

        Others.ItemsSource = null;
        Others.ItemsSource = theirs;

        Show(OwnHeader, OwnNote, searching || ours.Count > 0);
        Show(OthersHeader, OthersNote, theirs.Count > 0);

        NothingFound.Text = $"Пресетов с «{needle}» в имени нет.";
        NothingFound.Visibility = searching && ours.Count == 0 && theirs.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Redraw();

    /// <summary>Ширина, ниже которой плитки параметров встают друг под друга.</summary>
    private const double ParamsSideBySide = 760;

    /// <summary>
    /// Плитки «Параметров работы» рядом или друг под другом.
    /// </summary>
    /// <remarks>
    /// По ширине самой сетки, а не окна: окно масштабируется («Шрифт и размер»),
    /// и считать надо в тех точках, в которых плитки и раскладываются.
    /// </remarks>
    private void OnParamsSize(object sender, SizeChangedEventArgs e)
    {
        bool side = ParamsGrid.ActualWidth >= ParamsSideBySide;

        Grid.SetRow(CaptureTile, side ? 0 : 1);
        Grid.SetColumn(CaptureTile, side ? 2 : 0);
        Grid.SetColumnSpan(CaptureTile, side ? 1 : 3);
        Grid.SetColumnSpan(GameFilterTile, side ? 1 : 3);
        CaptureTile.Margin = new Thickness(0, side ? 0 : 12, 0, 0);
    }

    private static void Show(UIElement header, UIElement note, bool visible)
    {
        header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        note.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Каталог рецептов и вердикт по каждому.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Приёмы читаются с диска при каждом показе. Модули приходят вместе
    /// с движком, обновляются с ним, а подключить их можно и прямо отсюда —
    /// список, сложенный однажды, показывал бы вчерашнюю правду.
    /// </para>
    /// <para>
    /// Ошибки чтения не срывают вкладку: без приёмов каталог остаётся
    /// перечнем без вердиктов, и это честнее пустого места.
    /// </para>
    /// </remarks>
    private void ShowCatalog(AppSettings settings)
    {
        ZapretPreset? preset = null;
        IReadOnlyDictionary<string, string> providers =
            new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (ZapretPaths.Discover() is { } paths)
                providers = LuaModules.Providers(LuaModules.Scan(paths.LuaDirectory));

            if (settings.PresetName is { Length: > 0 } name
                && ZapretPaths.FindPreset(name) is { } file)
            {
                preset = new PresetReader().Load(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Пусть каталог будет без вердиктов.
        }

        var rows = CatalogRows.Build(preset, providers);
        int ready = rows.Count(r => r.Complaint.Length == 0);

        Catalog.ItemsSource = rows;

        CatalogNote.Text = preset is null
            ? $"Рецептов в каталоге: {rows.Count}. Выберите пресет, чтобы увидеть, какие из них он потянет."
            : providers.Count == 0
                ? $"Рецептов в каталоге: {rows.Count}. Модули движка не читаются, поэтому доступность не проверена."
                : $"Рецептов в каталоге: {rows.Count}, из них «{settings.DescribePreset()}» "
                    + $"потянет {ready}. Выбираются рецепты не здесь, а для отдельного имени — "
                    + "в разделе «Маршруты».";
    }

    /// <summary>
    /// Разворачивает каталог рецептов.
    /// </summary>
    /// <remarks>
    /// Тем же приёмом, что «Свой домен» и «Порядок вычисления» в маршрутах:
    /// два разных способа свернуть на одном экране читались бы как два
    /// разных вида карточек.
    /// </remarks>
    private void OnCatalogToggle(object sender, RoutedEventArgs e)
    {
        var open = Catalog.Visibility != Visibility.Visible;

        Catalog.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Chevrons.Turn(CatalogChevron, open);
    }

    /// <summary>
    /// Дописать пресету недостающий модуль.
    /// </summary>
    /// <remarks>
    /// Со спросом. Правка чужого файла — не то, что делают в ответ
    /// на нажатие, а среди пресетов есть доставшиеся от Zapret, за состав
    /// которых мы не отвечаем.
    /// </remarks>
    private void OnConnectModule(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }
            || Catalog.ItemsSource is not IEnumerable<CatalogRow> rows)
        {
            return;
        }

        if (rows.FirstOrDefault(r => r.Id == id) is not { Missing: { } module })
            return;

        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (settings.PresetName is not { Length: > 0 } name
            || ZapretPaths.FindPreset(name) is not { } file)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Дописать в пресет «{settings.DescribePreset()}» строку подключения модуля {module}?\n\n"
                + "Меняется одна строка в шапке файла, остальное остаётся как есть. "
                + "Новые рецепты станут доступны при следующем запуске движков.",
            "Подключить модуль",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
            return;

        try
        {
            Status.Text = PresetModules.Connect(file, module) switch
            {
                PresetModules.Result.Connected =>
                    $"Модуль {module} подключён. Рецепты из него заработают после перезапуска движков.",
                PresetModules.Result.Already =>
                    $"Модуль {module} и так подключён — файл не тронут.",
                _ =>
                    $"В пресете нет ни одной строки --lua-init, дописывать не к чему. "
                        + "Откройте его в редакторе.",
            };
        }
        catch (Exception ex)
        {
            Status.Text = $"Модуль {module} подключить не вышло: {ex.GetBaseException().Message}";
        }

        ShowCatalog(settings);
    }

    private void ShowChosen(AppSettings settings)
    {
        ChosenName.Text = settings.DescribePreset();

        ChosenDetail.Text = settings.Mode == OperatingMode.Off
            ? "Режим «выключено»: десинк не запустится, какой бы пресет ни стоял."
            : settings.PresetName is null
                ? "Пакеты не правятся. Всё, что закрыто по имени, останется закрытым — кроме того, что уведено через VPN."
                : "Применяется при запуске движков в разделе «Состояние».";

        Recommended.Visibility = string.Equals(settings.PresetName, AppSettings.DefaultPresetName,
            StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Открывать нечего, пока пресет не выбран: карточка в этом состоянии
        // описывает отсутствие выбора, а не файл.
        EditButton.IsEnabled = settings.PresetName is not null;

        // Подсказка перехвата говорит о выбранном пресете — сменился он,
        // сменилось и то, какие его секции останутся без трафика.
        ShowCapture(settings);
    }

    /// <summary>Уровни перехвата в порядке списка: от узкого к широкому.</summary>
    private static readonly CaptureWidth[] CaptureOrder = [CaptureWidth.Sites, CaptureWidth.Preset, CaptureWidth.All];

    /// <summary>Идёт заполнение списка — выбор не записывать.</summary>
    private bool _fillingCapture;

    private void OnCapture(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCapture || CaptureBox.SelectedIndex < 0)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var width = CaptureOrder[CaptureBox.SelectedIndex];

            // Выбор того же самого — не выбор.
            if (settings.Capture == width)
                return;

            var next = settings with { Capture = width };

            next.Save(AppSettings.DefaultPath);
            ShowCapture(next);

            Status.Text = $"Перехват: {PresetCapture.Word(width)}. Действует со следующего запуска движков.";
            this.Offer($"Перехват: {PresetCapture.Word(width)}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Ставит выбор в списке и пишет, что выйдет на деле с выбранным пресетом.</summary>
    private void ShowCapture(AppSettings settings)
    {
        // Заполняем, не поднимая события выбора: иначе показ тут же
        // записал бы состояние обратно и предложил перезапуск.
        _fillingCapture = true;
        CaptureBox.SelectedIndex = Array.IndexOf(CaptureOrder, settings.Capture);
        _fillingCapture = false;

        CaptureHint.Text = DescribeCapture(settings);
    }

    /// <summary>
    /// Порты перехвата и секции, которые при этом уровне потеряют трафик.
    /// </summary>
    /// <remarks>
    /// Секция, чьи порты вне перехвата, не получает трафика на них вовсе,
    /// а снаружи это неотличимо от неработающего рецепта. Сказать об этом
    /// должно окно, до запуска, а не человек через вечер разбора.
    /// </remarks>
    private static string DescribeCapture(AppSettings settings)
    {
        if (settings.PresetName is not { } name)
            return "Пресет не выбран — десинк не запускается.";

        if (ZapretPaths.FindPreset(name) is not { } path)
            return $"Пресет «{name}» не найден в папке пресетов — что выйдет с перехватом, сказать нечем.";

        ZapretPreset preset;

        try
        {
            preset = new PresetReader().Load(path);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        if (PresetCapture.Effective(preset, settings.Capture) is not { } ports)
            return $"У пресета «{preset.Name}» полный фильтр перехвата (--wf-raw) — уровень к нему не применяется.";

        var text = $"Сейчас: TCP {ports.Tcp}, UDP {ports.Udp}"
            + (settings.GameFilter ? $"; game filter добавит {GameFilter.Ports}." : ".");

        var lost = PresetCapture.Unreached(preset, settings.Capture);

        if (lost.Count > 0)
            text += $"\nСекции «{preset.Name}», которые не получат трафик на этих портах: {string.Join("; ", lost)}.";

        return text;
    }

    /// <summary>
    /// Проверяет, есть ли чем применять пресет.
    /// </summary>
    /// <remarks>
    /// Пресеты наши, а движок — из установки Zapret. Без неё выбор
    /// сохраняется и не делает ничего, и молчать об этом нельзя: человек
    /// выберет пресет, увидит его в «Состоянии» и решит, что десинк работает.
    /// </remarks>
    private void ShowEngine()
    {
        var paths = ZapretPaths.Discover();

        if (paths is not null && File.Exists(paths.ExecutablePath))
        {
            MissingCard.Visibility = Visibility.Collapsed;
            return;
        }

        MissingCard.Visibility = Visibility.Visible;
        MissingTitle.Text = "Движок десинка не найден";

        MissingBody.Text = paths is null
            ? "Установка Zapret не обнаружена. Пресет выберется и сохранится, но применять его нечем: "
              + "winws2.exe и списки доменов лежат в ней."
            : $"Каталог найден ({paths.Root}), а winws2.exe в нём нет — ожидался в подпапке exe. "
              + "Возможно, антивирус увёз его в карантин: WinDivert рядом с ним помечается как RiskTool.";
    }

    /// <summary>Меню «…» у строки пресета.</summary>
    private void OnRowMore(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string file } anchor)
            return;

        var path = Path.Combine(ZapretPaths.PresetDirectory, file);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(MenuEntry("", "Открыть в редакторе", () =>
        {
            Open(path);
            Status.Text = $"Открыт {file}. Правка подействует после перезапуска движков.";
        }));

        menu.Items.Add(MenuEntry("", "Показать файл в папке", () =>
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"" })));

        menu.IsOpen = true;
    }

    private MenuItem MenuEntry(string glyph, string text, Action act)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        var item = new MenuItem { Header = text, Icon = icon };

        item.Click += (_, _) =>
        {
            try
            {
                act();
            }
            catch (Exception ex)
            {
                Status.Text = "Не вышло: " + ex.GetBaseException().Message;
            }
        };

        return item;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ZapretPaths.PresetDirectory);

            Process.Start(new ProcessStartInfo
            {
                FileName = ZapretPaths.PresetDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть папку: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Открывает выбранный пресет в редакторе по умолчанию.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пресет правится руками: в нём порядок секций, от которого зависит,
    /// до какой дойдёт очередь, и рецепты, подобранные замерами. Путь
    /// «открыть папку → найти нужный файл среди дюжины» проходился каждый раз
    /// заново, причём имя файла и название пресета внутри него совпадают
    /// не всегда — промахнуться было легко.
    /// </para>
    /// <para>
    /// Файл ищется тем же <see cref="ZapretPaths.FindPreset"/>, которым его
    /// находит запуск движков. Иначе открылся бы один файл, а применялся
    /// другой — и правка «не действовала» бы без всякого объяснения.
    /// </para>
    /// </remarks>
    private void OnOpenPreset(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = AppSettings.Load(AppSettings.DefaultPath).PresetName;

            if (name is null)
            {
                Status.Text = "Пресет не выбран — открывать нечего.";
                return;
            }

            var path = ZapretPaths.FindPreset(name);

            if (path is null || !File.Exists(path))
            {
                Status.Text = $"Файл пресета «{name}» не найден в {ZapretPaths.PresetDirectory}.";
                return;
            }

            Open(path);

            // Про перезапуск сказано прямо: winws2 читает пресет один раз
            // при старте, и правка в открытом файле сама по себе не меняет
            // ничего. Без этой строки исправленная секция выглядит нерабочей.
            Status.Text = $"Открыт {Path.GetFileName(path)}. "
                + "Правка подействует после перезапуска движков в разделе «Состояние».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть пресет: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Отдаёт файл системе, а при отказе — «Блокноту».
    /// </summary>
    /// <remarks>
    /// Через оболочку, чтобы открылся редактор, которым человек правит текст,
    /// а не навязанный нами. Но у .txt может не быть сопоставленной программы
    /// вовсе — тогда оболочка отвечает отказом, и «Блокнот» здесь лучше
    /// сообщения об ошибке: он есть в любой Windows.
    /// </remarks>
    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{path}\"" });
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ours = Dropped(e).Count > 0;

        e.Effects = ours ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        DropHint.Visibility = ours ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropHint.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Кладёт принесённые файлы в папку пресетов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Копируем, а не переносим и не ссылаемся: человек тащит файл из папки
    /// загрузок, которую однажды почистит, — а пресет к тому времени станет
    /// тем, чем держится весь обход.
    /// </para>
    /// <para>
    /// Разбор до копирования, а не после. Файл, который не читается нашим
    /// разбором, лёг бы в папку и молча не появился в списке — и человек
    /// решил бы, что перетаскивание не работает вовсе.
    /// </para>
    /// </remarks>
    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;

        var files = Dropped(e);

        if (files.Count == 0)
            return;

        var taken = new List<string>();
        var refused = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var preset = new PresetReader().Load(file);

                if (preset.Sections.Count == 0)
                {
                    refused.Add($"{Path.GetFileName(file)} — ни одной секции");
                    continue;
                }

                Directory.CreateDirectory(ZapretPaths.PresetDirectory);

                var target = Path.Combine(ZapretPaths.PresetDirectory, Path.GetFileName(file));

                // Чужой файл не затираем молча: одноимённый пресет мог быть
                // тем, на котором сейчас держится обход.
                if (File.Exists(target)
                    && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(file),
                        StringComparison.OrdinalIgnoreCase))
                {
                    var answer = MessageBox.Show(
                        $"Пресет «{Path.GetFileNameWithoutExtension(file)}» уже есть. Заменить?",
                        "NetZapret",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (answer != MessageBoxResult.Yes)
                    {
                        refused.Add($"{Path.GetFileName(file)} — оставлен прежний");
                        continue;
                    }
                }

                File.Copy(file, target, overwrite: true);
                taken.Add(preset.Name);
            }
            catch (Exception ex)
            {
                refused.Add($"{Path.GetFileName(file)} — {ex.GetBaseException().Message}");
            }
        }

        Reload();

        Status.Text = (taken.Count, refused.Count) switch
        {
            (0, 0) => Status.Text,
            (_, 0) => $"Добавлено: {string.Join(", ", taken)}.",
            (0, _) => "Не взято: " + string.Join("; ", refused),
            _ => $"Добавлено: {string.Join(", ", taken)}. Не взято: {string.Join("; ", refused)}",
        };
    }

    /// <summary>Файлы .txt из того, что принесли; всё прочее пресетом быть не может.</summary>
    private static IReadOnlyList<string> Dropped(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return [];

        return e.Data.GetData(DataFormats.FileDrop) is not string[] files
            ? []
            : files
                .Where(f => File.Exists(f)
                    && Path.GetExtension(f).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
            Save(name);
    }

    private void Save(string name)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { PresetName = name };
            settings.Save(AppSettings.DefaultPath);

            ShowChosen(settings);

            // Каталог говорит о выбранном пресете — сменился он, сменился и счёт.
            ShowCatalog(settings);

            foreach (var row in _rows)
                row.Chosen = string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase);

            Redraw();

            Status.Text = $"Выбран «{name}». Применится при следующем запуске движков.";

            this.Offer($"Выбран пресет «{name}»");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    private static string Ending(int count, string one, string few, string many)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return many;

        return (count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}
