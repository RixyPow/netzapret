using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Пресет в списке выбора.</summary>
public sealed record PresetRow(string Name, string Version, string Fake)
{
    /// <summary>
    /// Имя файла — то, чем строка отличается от соседней.
    /// </summary>
    /// <remarks>
    /// Порядок раскладывается по нему, а не по названию пресета: три пресета
    /// Zapret объявляют себя «Universal V5», и раскладка по названию роняла
    /// весь список жалобой на повторный ключ.
    /// </remarks>
    public required string File { get; init; }

    public required string Detail { get; set; }

    public bool Chosen { get; set; }

    /// <summary>Над строкой держат перетаскиваемую — подсвечиваем место высадки.</summary>
    public bool Over { get; set; }

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
        (Brush)Application.Current.FindResource(Over ? "Warn" : Chosen ? "Accent" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;

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

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowChosen(settings);
        ShowEngine();

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

            var rows = PresetOrder.Apply(
                new PresetReader()
                    .Read(files)
                    .Select(preset => Row(preset, settings.PresetName))
                    .ToList(),
                row => row.File);

            _rows = rows;
            Redraw();

            Status.Text = $"Пресетов: {rows.Count}, из них наших {rows.Count(r => r.Official)}. "
                + "Выбор применяется при следующем запуске движков; порядок внутри списка "
                + "меняется перетаскиванием за ручку слева.";

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
    private void StartCounting(IReadOnlyList<PresetRow> rows)
    {
        var root = ZapretPaths.Discover()?.Root;

        if (root is null)
            return;

        _counting?.Cancel();
        _counting = new CancellationTokenSource();

        var token = _counting.Token;

        _ = Task.Run(() =>
        {
            var reader = new PresetReader();

            foreach (var row in rows)
            {
                if (token.IsCancellationRequested)
                    return;

                // По имени файла, а не по названию пресета: названия у разных
                // файлов совпадают, и поиск по ним считал бы покрытие одного
                // и того же трижды.
                var path = Path.Combine(ZapretPaths.PresetDirectory, row.File);

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

                if (token.IsCancellationRequested)
                    return;

                Dispatcher.Invoke(() =>
                {
                    row.Detail += $" · {domains} доменов, {addresses} подсетей";
                    Redraw();
                });
            }
        }, token);
    }

    /// <summary>
    /// Раскладывает строки по двум спискам.
    /// </summary>
    /// <remarks>
    /// Пустая половина скрывается вместе с заголовком: подпись «Пресеты
    /// сообщества» над пустотой обещает то, чего нет.
    /// </remarks>
    private void Redraw()
    {
        var ours = _rows.Where(row => row.Official).ToList();
        var theirs = _rows.Where(row => !row.Official).ToList();

        Own.ItemsSource = null;
        Own.ItemsSource = ours;

        Others.ItemsSource = null;
        Others.ItemsSource = theirs;

        Show(OwnHeader, OwnNote, ours.Count > 0);
        Show(OthersHeader, OthersNote, theirs.Count > 0);
    }

    private static void Show(UIElement header, UIElement note, bool visible)
    {
        header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        note.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowChosen(AppSettings settings)
    {
        ChosenName.Text = settings.DescribePreset();

        ChosenDetail.Text = settings.Mode == OperatingMode.Off
            ? "Режим «выключено»: десинк не запустится, какой бы пресет ни стоял."
            : settings.PresetName is null
                ? "Пакеты не правятся. Всё, что закрыто по имени, останется закрытым — кроме того, что уведено через VPN."
                : "Применяется при запуске движков в разделе «Состояние».";

        OffButton.IsEnabled = settings.PresetName is not null;
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

    /// <summary>
    /// Начинает перенос строки.
    /// </summary>
    /// <remarks>
    /// От ручки, а не от всей строки: строка нажатием выбирает пресет,
    /// и совмещать в одном месте выбор и перенос значит промахиваться
    /// то одним, то другим.
    /// </remarks>
    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PresetRow row })
            return;

        // Событие съедается, иначе нажатие дойдёт до кнопки и выберет пресет,
        // который человек всего лишь собирался переставить.
        e.Handled = true;

        DragDrop.DoDragDrop((DependencyObject)sender, row, DragDropEffects.Move);
    }

    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PresetRow row }
            || e.Data.GetData(typeof(PresetRow)) is not PresetRow moving)
        {
            return;
        }

        e.Handled = true;

        // Через границу списков не переставляем: место строки задаётся именем
        // пресета, и «перенесённая» вернулась бы к своим на первом же заходе.
        bool same = row.Official == moving.Official;

        e.Effects = same ? DragDropEffects.Move : DragDropEffects.None;

        bool over = same && !ReferenceEquals(row, moving);

        if (row.Over == over)
            return;

        row.Over = over;
        Redraw();
    }

    private void OnRowDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PresetRow row } || !row.Over)
            return;

        row.Over = false;
        Redraw();
    }

    /// <summary>
    /// Ставит перенесённую строку на место той, на которую её уронили.
    /// </summary>
    /// <remarks>
    /// Порядок сохраняется сразу: перетаскивание — жест без кнопки
    /// «применить», и разложенный список, вернувшийся при следующем заходе
    /// к прежнему виду, выглядел бы поломкой.
    /// </remarks>
    private void OnRowDrop(object sender, DragEventArgs e)
    {
        foreach (var each in _rows)
            each.Over = false;

        if (sender is not FrameworkElement { DataContext: PresetRow target }
            || e.Data.GetData(typeof(PresetRow)) is not PresetRow moving
            || ReferenceEquals(target, moving)
            || target.Official != moving.Official)
        {
            Redraw();
            return;
        }

        e.Handled = true;

        var rows = _rows.ToList();
        int from = rows.IndexOf(moving);
        int to = rows.IndexOf(target);

        if (from < 0 || to < 0)
            return;

        rows.RemoveAt(from);
        rows.Insert(to, moving);

        _rows = rows;
        Redraw();

        try
        {
            PresetOrder.Save(rows.Select(row => row.File));

            int place = rows.Where(row => row.Official == moving.Official).ToList().IndexOf(moving) + 1;
            Status.Text = $"Порядок сохранён: «{moving.Name}» теперь {place}-й в своём списке.";
        }
        catch (Exception ex)
        {
            Status.Text = "Порядок не записался: " + ex.GetBaseException().Message;
        }
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

    private void OnOff(object sender, RoutedEventArgs e) => Save(null);

    private void Save(string? name)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { PresetName = name };
            settings.Save(AppSettings.DefaultPath);

            ShowChosen(settings);

            {
                foreach (var row in _rows)
                    row.Chosen = string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase);

                Redraw();
            }

            Status.Text = name is null
                ? "Десинк выключен. Применится при следующем запуске движков."
                : $"Выбран «{name}». Применится при следующем запуске движков.";

            this.Offer(name is null ? "Десинк выключен" : $"Выбран пресет «{name}»");
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
