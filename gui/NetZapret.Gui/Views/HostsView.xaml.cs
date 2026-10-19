using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>Строка карточки «Источники записей».</summary>
public sealed record HostsSourceLine(string Name, int Count, Brush Back);

/// <summary>
/// Файл hosts: что прибито, кем, живо ли оно.
/// </summary>
/// <remarks>
/// <para>
/// С 19.10 — одна таблица на весь файл по макету владельца. Прежде было два
/// списка: наш блок и свёрнутая карточка «Чужие записи» со своей прокруткой
/// в 360 точек, а чужих у владельца ~600. Чужие показываются с 17.09:
/// пины на Canva, RuTracker и LinkedIn выглядели как неисправный VPN,
/// а разбор стоил дня.
/// </para>
/// <para>
/// Удаляются и наши записи, и чужие — по одной, выбранные или найденные
/// поиском, всегда с вопросом и с копией файла рядом. Удаление выбранных
/// для чужих — слово владельца 19.10 («делаем»); прежде чужие уходили по
/// одной или найденные поиском (26.09), а «почистить всё» не было вовсе.
/// «Снять» и «удалить» с 19.10 одно слово — «удалить»: для файла это одно
/// действие, а разница была только в том, кто вёл строку, и её показывает
/// колонка «Источник» и вопрос перед удалением.
/// </para>
/// <para>
/// Проверка адресов нужна оттого, что пин стареет молча: адрес сети доставки
/// живёт днями, чужой прокси — пока его содержат. Когда узел умолкает,
/// прибитое имя перестаёт открываться, а в файле стоит уверенная строка,
/// и чинить начинают не с того конца.
/// </para>
/// </remarks>
public partial class HostsView : UserControl
{
    /// <summary>Тело уже — правая колонка уходит под таблицу.</summary>
    /// <remarks>
    /// Таблице нужно ~560 точек на семь колонок, колонке — 320.
    /// </remarks>
    private const double NarrowBelow = 900;

    private CancellationTokenSource? _work;

    /// <summary>Весь файл строками; показывается отобранное.</summary>
    private IReadOnlyList<HostsLine> _all = [];

    /// <summary>Показанное сейчас — на него действует «выбрать всё».</summary>
    private IReadOnlyList<HostsLine> _shown = [];

    /// <summary>Ответы проверки по адресу: переживают перечитывание файла после правки.</summary>
    private readonly Dictionary<string, HostsHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    private bool _checked;

    private HostsShow _show = HostsShow.All;

    private string? _source;

    private HostsDuplicateReport? _repeats;

    private bool _narrow;

    private bool _sideOpen;

    public HostsView()
    {
        InitializeComponent();

        ShowTab(list: false);
        PathText.Text = HostsFile.DefaultPath;

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _work?.Cancel();
    }

    private void Reload()
    {
        if (HostsEditor.WhoReplaced() is { } who)
        {
            ReplacedCard.Visibility = Visibility.Visible;
            ReplacedTitle.Text = $"Файл hosts переписан: {who}";
        }
        else
        {
            ReplacedCard.Visibility = Visibility.Collapsed;
        }

        ShowRepeats();

        try
        {
            // Выбор переживает перечитывание: выключили выбранное — оно
            // и осталось выбранным, можно тут же включить обратно.
            var selected = _all.Where(l => l.Selected).Select(Key).ToHashSet();

            _all = HostsTable.Read()
                .Select(row => new HostsLine(row)
                {
                    Health = _health.GetValueOrDefault(row.Address),
                    Selected = selected.Contains((row.Line, row.Name)),
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _all = [];
            Say("Файл не читается: " + ex.GetBaseException().Message);
        }

        ShowCounts();
        ShowSources();
        Filter();
    }

    private static (int, string) Key(HostsLine line) => (line.Row.Line, line.Row.Name);

    private void ShowCounts()
    {
        TotalText.Text = _all.Count.ToString();
        OursText.Text = _all.Count(l => l.Row.Ours).ToString();
        OffText.Text = _all.Count(l => !l.Row.Enabled).ToString();

        if (!_checked)
        {
            DeadText.Text = "—";
            DeadCaption.Text = "молчат: не проверено";
            DeadGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            return;
        }

        int dead = _all.Count(l => l.Row.Enabled && l.Health == HostsHealth.Dead);

        DeadText.Text = dead.ToString();
        DeadCaption.Text = "молчат";
        DeadGlyph.SetResourceReference(TextBlock.ForegroundProperty, dead > 0 ? "Danger" : "Accent");
    }

    private void ShowSources()
    {
        var lines = _all
            .GroupBy(l => l.Source)
            .OrderByDescending(g => g.Key == HostsTable.OurSource)
            .ThenByDescending(g => g.Count())
            .Select(g => new HostsSourceLine(
                g.Key,
                g.Count(),
                g.Key == _source ? (Brush)FindResource("Raised") : Brushes.Transparent))
            .ToList();

        Sources.ItemsSource = lines;
    }

    private void OnSourceClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HostsSourceLine line })
            return;

        _source = _source == line.Name ? null : line.Name;
        ShowSources();
        Filter();
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Filter();

    private void Filter()
    {
        _shown = HostsFilter.Apply(_all, Search.Text, _show, _source);
        Rows.ItemsSource = _shown;

        ShowButton.Content = HostsFilter.Word(_show) + (_source is null ? string.Empty : " · " + _source);

        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "В файле нет ни одной записи."
            : "Ничего не подошло.";

        ShowSelection();
    }

    private void OnShowMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ShowButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        foreach (var show in Enum.GetValues<HostsShow>())
        {
            int count = _all.Count(l => HostsFilter.Shows(l, show));

            // «Молчат» до проверки — пустое обещание: говорим, что не проверяли.
            var text = show == HostsShow.Dead && !_checked
                ? "Молчат — сначала «Проверить адреса»"
                : $"{HostsFilter.Word(show)} — {count}";

            var item = new MenuItem { Header = text, IsChecked = show == _show, IsEnabled = show != HostsShow.Dead || _checked };
            item.Click += (_, _) =>
            {
                _show = show;
                Filter();
            };

            menu.Items.Add(item);
        }

        if (_source is not null)
        {
            menu.Items.Add(new Separator());

            var all = new MenuItem { Header = "Все источники" };
            all.Click += (_, _) =>
            {
                _source = null;
                ShowSources();
                Filter();
            };

            menu.Items.Add(all);
        }

        menu.IsOpen = true;
    }

    // --- Выбор ---

    private IReadOnlyList<HostsLine> Selected => _all.Where(l => l.Selected).ToList();

    private void OnRowTick(object sender, RoutedEventArgs e) => ShowSelection();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        bool select = AllBox.IsChecked == true;

        foreach (var line in _shown)
            line.Selected = select;

        ShowSelection();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var line in _all)
            line.Selected = false;

        ShowSelection();
    }

    private void ShowSelection()
    {
        var selected = Selected;
        int count = selected.Count;
        int hidden = count - _shown.Count(l => l.Selected);

        // Выбранное и скрытое отбором называем: иначе «Удалить» снесло бы
        // строки, которых человек на экране уже не видит.
        SelectedText.Text = hidden > 0
            ? $"Выбрано: {count}, из них скрыто отбором {hidden}"
            : $"Выбрано: {count}";

        DeleteButton.IsEnabled = count > 0;
        EnableButton.IsEnabled = selected.Any(l => !l.Row.Enabled);
        DisableButton.IsEnabled = selected.Any(l => l.Row.Enabled);
        ClearSelectionButton.IsEnabled = count > 0;

        AllBox.IsChecked = _shown.Count > 0 && _shown.All(l => l.Selected);
    }

    // --- Правки ---

    private void OnDeleteSelected(object sender, RoutedEventArgs e) => Delete(Selected);

    private void OnEnableSelected(object sender, RoutedEventArgs e) => Toggle(Selected, enabled: true);

    private void OnDisableSelected(object sender, RoutedEventArgs e) => Toggle(Selected, enabled: false);

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HostsLine line })
            return;

        Toggle([line], enabled: !line.Row.Enabled);
    }

    /// <summary>Включает или выключает записи — без вопроса: всё возвращается тем же выключателем.</summary>
    private void Toggle(IReadOnlyList<HostsLine> lines, bool enabled)
    {
        var targets = lines.Where(l => l.Row.Enabled != enabled).Select(Key).ToList();

        if (targets.Count == 0)
            return;

        Edit(
            () =>
            {
                var (_, changed) = HostsEditor.SetEnabled(targets, enabled);
                return enabled ? $"Включено: {changed}." : $"Отключено: {changed}. Записи остались в файле под решёткой.";
            },
            "Не удалось " + (enabled ? "включить" : "отключить"));
    }

    /// <summary>
    /// Удаляет записи из файла — наши и чужие, одной правкой и одной копией.
    /// </summary>
    /// <remarks>
    /// Вопрос один на всё, с числом, примерами и числом чужих: число без имён
    /// не даёт заметить, что выбор зацепил лишнее, а чужую запись может
    /// вернуть её хозяин — или держать на ней чью-то работу.
    /// </remarks>
    private void Delete(IReadOnlyList<HostsLine> lines)
    {
        if (lines.Count == 0)
            return;

        var names = lines.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sample = string.Join("\n", lines.Take(5).Select(l => $"  {l.Name} → {l.Address}"))
            + (lines.Count > 5 ? $"\n  … и ещё {lines.Count - 5}" : string.Empty);

        var foreign = lines.Where(l => !l.Row.Ours).ToList();
        var owners = foreign.Select(l => l.Source).Distinct().ToList();

        var about = foreign.Count == 0
            ? "Все они поставлены NetZapret."
            : $"Чужих среди них {foreign.Count} ({string.Join(", ", owners)}): их ведёт не NetZapret, "
              + "и программа-хозяин может вернуть их при своей следующей правке.";

        var answer = MessageBox.Show(
            (lines.Count == 1
                ? $"Удалить запись «{lines[0].Name} → {lines[0].Address}» из файла hosts?"
                : $"Удалить из файла hosts записей: {lines.Count}?\n\n{sample}")
            + $"\n\n{about}\n\nОтменить нажатием будет нельзя. Копия файла ляжет рядом — из неё можно вернуть всё целиком.",
            "NetZapret",
            MessageBoxButton.YesNo,
            foreign.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        var targets = lines.Select(Key).ToList();

        Edit(
            () =>
            {
                var (backup, removed) = HostsEditor.RemoveNames(targets);

                // Удалённое не должно остаться выбранным под чужим номером строки.
                foreach (var line in lines)
                    line.Selected = false;

                return removed == lines.Count
                    ? $"Удалено: {removed}. Копия прежнего файла: {backup}"
                    : $"Удалено: {removed} из {lines.Count} — остальных в тех строках уже не было, файл менялся. Копия: {backup}";
            },
            "Не удалось удалить");
    }

    /// <summary>
    /// Правка файла, сброс кэша DNS и перечитывание.
    /// </summary>
    /// <remarks>
    /// Сброс — в фоне: <c>ipconfig /flushdns</c> ждётся до 10 с, и в потоке
    /// окна он однажды и дал секундную запись сторожа подвисаний (см. «Маршруты»).
    /// </remarks>
    private async void Edit(Func<string> edit, string failure)
    {
        string said;

        try
        {
            said = edit();
        }
        catch (Exception ex)
        {
            Say(failure + ": " + ex.GetBaseException().Message);
            return;
        }

        Reload();
        Say(said);

        await Task.Run(HostsEditor.FlushDns);
    }

    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HostsLine line } button)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        Add(menu, line.Row.Enabled ? "Отключить" : "Включить", () => Toggle([line], !line.Row.Enabled));
        Add(menu, "Удалить", () => Delete([line]));

        // Вид меняется только у своих: пометку «адрес сервиса» щит верит
        // лишь нашему блоку, чужой строке её писать бессмысленно.
        if (line.Row.Ours && line.Row.Enabled && line.Row.Kind != HostsPinKind.Stub)
        {
            menu.Items.Add(new Separator());

            if (line.Row.Kind == HostsPinKind.Service)
                Add(menu, "Это посредник — пропускать мимо десинка", () => SetKind(line, service: false));
            else
                Add(menu, "Это адрес сервиса — оставить десинку", () => SetKind(line, service: true));
        }

        menu.Items.Add(new Separator());
        Add(menu, "Все записи на этом адресе", () => Search.Text = line.Address);
        Add(menu, "Скопировать строку", () => Copy($"{line.Address} {line.Name}"));

        menu.IsOpen = true;
    }

    private static void Add(ContextMenu menu, string text, Action act)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => act();
        menu.Items.Add(item);
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Say("Скопировано: " + text);
        }
        catch (Exception)
        {
            // Буфер обмена бывает занят другой программой; повторное нажатие поможет.
            Say("Буфер обмена занят — попробуйте ещё раз.");
        }
    }

    /// <summary>Меняет вид пина у имени — все его адреса в нашем блоке разом.</summary>
    /// <remarks>
    /// Пометка стоит у имени, а не у строки: щит берёт или не берёт имя
    /// целиком, и два адреса одного имени разного вида смысла не имеют.
    /// </remarks>
    private void SetKind(HostsLine line, bool service)
    {
        Edit(
            () =>
            {
                if (!HostsEditor.PinsAll().TryGetValue(line.Name, out var addresses))
                    return "Записи уже нет в нашем блоке — файл менялся.";

                var entry = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [line.Name] = addresses,
                };

                var result = service ? HostsEditor.PinWithDesync(entry) : HostsEditor.Repin(entry);

                return result.Reverted
                    ?? (service
                        ? $"{line.Name} — адрес сервиса: щит его больше не берёт, имя обработает рецепт десинка."
                        : $"{line.Name} — посредник: щит пропускает его мимо десинка.");
            },
            "Не удалось сменить вид");
    }

    // --- Добавление ---

    private bool _listTab;

    private void OnOneTab(object sender, RoutedEventArgs e) => ShowTab(list: false);

    private void OnListTab(object sender, RoutedEventArgs e) => ShowTab(list: true);

    private void ShowTab(bool list)
    {
        _listTab = list;
        OnePanel.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListPanel.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        OneTab.Style = (Style)FindResource(list ? "Ghost" : "Primary");
        ListTab.Style = (Style)FindResource(list ? "Primary" : "Ghost");
        AddButton.Content = list ? "Добавить списком" : "Добавить запись";
    }

    private void OnNewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            AddPins();
    }

    private void OnAddPin(object sender, RoutedEventArgs e) => AddPins();

    /// <summary>Прибивает введённое руками — одно имя или список.</summary>
    /// <remarks>
    /// Отдельно от окна пина, которое подбирает живой адрес пробами: здесь
    /// адрес уже известен, и подбирать нечего. Проверка строгая — строка,
    /// не разобравшаяся в адрес, ушла бы в файл и осталась там молча нерабочей.
    /// Запись ложится в наш блок в начале файла: при двух записях на одно имя
    /// Windows берёт первую, и дописанная в конец проиграла бы чужой молча.
    /// </remarks>
    private void AddPins()
    {
        IReadOnlyDictionary<string, string> pins;
        IReadOnlyList<string> bad = [];

        if (_listTab)
        {
            (pins, bad) = HostsTable.ParseList(NewList.Text);

            if (pins.Count == 0)
            {
                Say(bad.Count == 0
                    ? "Список пуст."
                    : $"Ни одна строка не разобралась — нужны адрес и имя, например «93.184.216.34 example.com». Первая: {bad[0]}");
                return;
            }
        }
        else
        {
            if (HostsTable.Name(NewName.Text) is not { } name)
            {
                Say("В поле «Имя» нужно имя вида example.com.");
                return;
            }

            if (!System.Net.IPAddress.TryParse(NewAddress.Text.Trim(), out var address))
            {
                Say("В поле «Адрес» нужен адрес вида 93.184.216.34 — имя там не подойдёт: "
                    + "файл hosts разрешает имена в адреса, а не в другие имена.");
                return;
            }

            pins = new Dictionary<string, string> { [name] = address.ToString() };
        }

        bool service = NewService.IsChecked == true;
        var entries = pins.ToDictionary(
            p => p.Key,
            p => (IReadOnlyList<string>)[p.Value],
            StringComparer.OrdinalIgnoreCase);

        Edit(
            () =>
            {
                var result = service
                    ? HostsEditor.PinWithDesync(entries, note: "вручную")
                    : HostsEditor.Repin(entries, note: "вручную");

                NewName.Clear();
                NewAddress.Clear();

                // Неразобранное остаётся в поле: его поправят и добавят ещё раз.
                NewList.Text = string.Join(Environment.NewLine, bad);

                var said = pins.Count == 1
                    ? $"Прибито: {pins.First().Key} → {pins.First().Value}."
                    : $"Прибито имён: {pins.Count}.";

                if (bad.Count > 0)
                    said += $" Не разобралось строк: {bad.Count} — они остались в поле.";

                // Про чужие записи на то же имя говорим сразу: пока они на месте,
                // имя разрешается дважды, и предсказать исход по файлу не выйдет.
                if (result.Shadowed.Count > 0)
                    said += $" На эти имена есть чужие записи ({result.Shadowed.Count}) — наша стоит выше и действует, "
                        + "но, удалив её, вы получите чужую.";

                return result.Reverted ?? said;
            },
            "Не удалось прибить");
    }

    // --- Повторы ---

    /// <summary>
    /// Карточка повторов: сколько, каких и что из них можно убрать.
    /// </summary>
    /// <remarks>
    /// Спорящие адреса называются отдельно и не убираются кнопкой. Сейчас
    /// они не действуют — выше то же имя прибито к другому, — но удаливший
    /// верхнюю запись получит нижнюю, и это решение человека, а не наше.
    /// </remarks>
    private void ShowRepeats()
    {
        try
        {
            _repeats = HostsDuplicates.Find();
        }
        catch (Exception)
        {
            _repeats = null;
        }

        if (_repeats is not { Any: true } report)
        {
            RepeatsCard.Visibility = Visibility.Collapsed;
            return;
        }

        RepeatsCard.Visibility = Visibility.Visible;
        RepeatsTitle.Text = $"Имена прибиты повторно: {report.Repeats.Count}";

        var parts = new List<string>();

        if (report.Same > 0)
            parts.Add($"{report.Same} — тот же адрес ещё раз: Windows берёт первую запись, повтор ничего не делает.");

        if (report.Conflicting > 0)
            parts.Add($"{report.Conflicting} — другой адрес: сейчас не действуют, но всплывут, если удалить запись выше.");

        if (report.ForeignBlocks.Count > 0)
            parts.Add($"Часть лежит в блоке {string.Join(", ", report.ForeignBlocks)} — он может вернуть их.");

        RepeatsText.Text = string.Join(" ", parts);

        RepeatsButton.Visibility = report.Removable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RepeatsButton.Content = $"Убрать повторы ({report.Removable.Count})";
    }

    private void OnShowRepeats(object sender, RoutedEventArgs e)
    {
        _show = HostsShow.Repeats;
        Filter();
    }

    private void OnRemoveRepeats(object sender, RoutedEventArgs e)
    {
        if (_repeats is not { Removable.Count: > 0 } report)
            return;

        var answer = MessageBox.Show(
            $"Удалить из файла hosts {report.Removable.Count} строк-повторов?\n\n"
            + "Каждая из них повторяет запись выше с тем же адресом, так что работать "
            + "всё будет как сейчас. Отменить нажатием будет нельзя.\n\n"
            + "Копия файла ляжет рядом — из неё можно вернуть всё целиком.",
            "NetZapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        Edit(
            () =>
            {
                // Файл перечитывается перед удалением: номера строк взяты при
                // показе карточки, а между показом и нажатием его мог переписать
                // кто угодно. Удалять по устаревшим номерам значило бы снять не то.
                var fresh = HostsDuplicates.Find();
                var backup = HostsEditor.Remove(fresh.Removable);

                return $"Убрано повторов: {fresh.Removable.Count}. Копия прежнего файла: {backup}";
            },
            "Не удалось убрать");
    }

    // --- Проверка ---

    /// <summary>Стучится в каждый адрес действующих записей и говорит, кто молчит.</summary>
    /// <remarks>
    /// Весь файл, а не только наш блок: на макете владельца 19.10 кружок
    /// живости у каждой строки, и мёртвый чужой пин ломает сайт так же,
    /// как наш. Ответы приходят по адресу за адресом и красят строки на месте.
    /// </remarks>
    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();

        CheckButton.IsEnabled = false;
        CheckButton.Content = "Проверяю…";

        try
        {
            var progress = new Progress<PinHealth>(health =>
            {
                var state = !health.Tested ? HostsHealth.Untested
                    : health.Alive ? HostsHealth.Alive
                    : HostsHealth.Dead;

                _health[health.Address] = state;

                foreach (var line in _all.Where(l => l.Address == health.Address))
                    line.Health = state;
            });

            var entries = HostsEditor.Parse();
            var results = await Task.Run(() => HostsEditor.CheckAsync(entries, _work.Token, progress));

            _checked = true;
            ShowCounts();

            int deadAddresses = results.Count(r => r.Tested && !r.Alive);
            int deadNames = _all.Count(l => l.Row.Enabled && l.Health == HostsHealth.Dead);

            Say(deadAddresses == 0
                ? $"Все {results.Count} адресов отвечают."
                : $"Молчат {deadAddresses} адресов из {results.Count} — на них {deadNames} записей. "
                  + "Такой пин ведёт в никуда и выглядит блокировкой: удалите его или подберите адрес заново. "
                  + "Показать их — «Молчат» в отборе.");
        }
        catch (OperationCanceledException)
        {
            Say("Проверка прервана.");
        }
        catch (Exception ex)
        {
            Say("Проверка не удалась: " + ex.GetBaseException().Message);
        }
        finally
        {
            CheckButton.IsEnabled = true;
            CheckButton.Content = "Проверить адреса";
        }
    }

    // --- Прочее ---

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            // Блокнотом, а не своим редактором: файл системный, и человек,
            // правящий его руками, должен видеть его целиком — с комментариями
            // и блоками, которых таблица не показывает.
            Process.Start(new ProcessStartInfo("notepad.exe", HostsFile.DefaultPath)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Say("Не удалось открыть: " + ex.GetBaseException().Message);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            // С выделенным файлом: рядом лежат копии, и hosts среди них теряется.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{HostsFile.DefaultPath}\"")
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Say("Не удалось открыть папку: " + ex.GetBaseException().Message);
        }
    }

    private void Say(string text)
    {
        Status.Text = text;
        Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// На узком окне правая колонка уходит под таблицу и открывается кнопкой.
    /// </summary>
    /// <remarks>
    /// Как сводка «Наблюдения»: открытая всегда, она отнимала бы у таблицы
    /// всё, кроме нескольких строк.
    /// </remarks>
    private void OnBodySize(object sender, SizeChangedEventArgs e)
    {
        _narrow = Body.ActualWidth < NarrowBelow;

        SideColumn.Width = new GridLength(_narrow ? 0 : 320);
        SideRow.Height = _narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetColumn(SideHost, _narrow ? 0 : 1);
        Grid.SetRow(SideHost, _narrow ? 1 : 0);
        SideHost.Margin = _narrow ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);

        SideButton.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        ShowSide();
    }

    private void OnSide(object sender, RoutedEventArgs e)
    {
        _sideOpen = !_sideOpen;
        ShowSide();
    }

    private void ShowSide()
    {
        bool shown = !_narrow || _sideOpen;

        SideHost.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SideRow.Height = _narrow && shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SideButton.Content = _sideOpen ? "Скрыть" : "Добавить запись";
    }
}
