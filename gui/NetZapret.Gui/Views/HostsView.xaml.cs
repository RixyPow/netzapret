using System.Diagnostics;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>Прибитое имя.</summary>
/// <param name="Ours">
/// Запись поставила программа. Только такие она и снимает: файл общий,
/// чужие записи ведёт кто-то ещё, и кнопка «снять» у них означала бы право,
/// которого у нас нет.
/// </param>
/// <param name="Line">
/// Номер строки в файле; у наших записей не используется.
/// </param>
/// <remarks>
/// Чужая строка правится по номеру, а не по содержимому: две записи могут
/// совпадать дословно, и удалять «такую же» значило бы снять не ту.
/// </remarks>
public sealed record PinRow(
    string Name,
    string Detail,
    string Note,
    Brush Color,
    bool Ours = true,
    int Line = -1)
{
    public Visibility UnpinShown => Ours ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ForeignShown => Ours ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Файл hosts: что прибито и живо ли оно.
/// </summary>
/// <remarks>
/// <para>
/// Два списка: наш блок и всё остальное. Файл ведёт не одна программа —
/// записи ставит редактор Zapret GUI, антивирус, рука, — и до 17.09 чужие
/// не показывались вовсе. Это выходило боком: пины на Canva, RuTracker
/// и LinkedIn выглядели как неисправный VPN, а разбор стоил дня.
/// </para>
/// <para>
/// Чужую запись можно удалить — решение владельца от 17.09, взамен прежнего
/// «не трогаем вовсе». Разом их не чистят: удаление по одной, с вопросом
/// перед каждым и копией файла рядом. Кнопки «почистить всё» нет и не будет
/// — она однажды сотрёт то, на чём всё держалось.
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
    private CancellationTokenSource? _work;

    public HostsView()
    {
        InitializeComponent();

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

        try
        {
            var pins = HostsEditor.Pins();

            _ours = pins
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new PinRow(
                    p.Key,
                    p.Value,
                    "не проверен",
                    (Brush)FindResource("Faint")))
                .ToList();

            OursNote.Text = pins.Count == 0
                ? "Программа сюда ничего не ставила."
                : "Эти записи поставила программа, и она же их снимает.";

            ShowForeign(pins);
            Filter();
        }
        catch (Exception ex)
        {
            Status.Text = "Файл не читается: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Показывает записи, которые в файле есть, а нашими не являются.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Видеть их надо, а трогать нельзя, и это два разных утверждения.
    /// Прежде окно показывало только свой блок — и чужой пин оставался
    /// невидим ровно тогда, когда объяснял происходящее: записи от редактора
    /// hosts из Zapret GUI на Canva, RuTracker и LinkedIn однажды выглядели
    /// как неисправный VPN, и разбор стоил дня.
    /// </para>
    /// <para>
    /// Свёрнуты по умолчанию: на живой машине их бывают сотни — на той,
    /// где это писалось, семьсот восемьдесят две против ста трёх наших.
    /// </para>
    /// </remarks>
    private void ShowForeign(IReadOnlyDictionary<string, string> ours)
    {
        try
        {
            // Через Parse, а не Read: нужен номер строки, иначе править нечего.
            // Read отвечает на вопрос «во что разрешится имя» и про файл
            // как таковой не знает.
            var foreign = HostsEditor.Parse()
                .Where(e => !e.Names.Any(ours.ContainsKey))
                .SelectMany(e => e.Names.Select(n => new PinRow(
                    n,
                    e.Address + (e.Enabled ? string.Empty : " · выключена"),
                    e.Note is { Length: > 0 } note ? note : "чужая",
                    (Brush)FindResource(e.Enabled ? "Faint" : "Muted"),
                    Ours: false,
                    Line: e.Line)))
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .ToList();

            _foreign = foreign;
        }
        catch (Exception)
        {
            // Файл системный и может быть занят. Свой блок при этом уже
            // показан — половина сведений лучше жалобы вместо них.
            _foreign = [];
        }
    }

    /// <summary>Наш блок целиком; показывается отобранное из него.</summary>
    private IReadOnlyList<PinRow> _ours = [];

    /// <summary>Чужие записи целиком.</summary>
    private IReadOnlyList<PinRow> _foreign = [];

    private void OnSearch(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        Filter();
    }

    /// <summary>
    /// Показывает то, что подошло под поиск.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ищет по обоим спискам разом. Разделение на своё и чужое полезно
    /// при осмотре, но мешает, когда ищешь одно имя и не знаешь, чьё оно, —
    /// а именно так в hosts и заглядывают: когда что-то сломалось.
    /// </para>
    /// <para>
    /// Найденное среди чужих раскрывает их карточку само. Свёрнутая,
    /// она спрятала бы ровно то, что человек искал, и поиск выглядел бы
    /// не нашедшим ничего.
    /// </para>
    /// </remarks>
    private void Filter()
    {
        var needle = Search.Text;
        bool searching = HostsFilter.Searching(needle);

        var ours = HostsFilter.Apply(_ours, needle);
        var foreign = HostsFilter.Apply(_foreign, needle);

        Pins.ItemsSource = ours;
        Foreign.ItemsSource = foreign;

        OursHeader.Visibility = ours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OursNote.Visibility = OursHeader.Visibility;

        ForeignToggle.Visibility = foreign.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ForeignSummary.Text = searching
            ? $"{foreign.Count} подошло из {_foreign.Count}."
            : $"{_foreign.Count} — их ведёт кто-то ещё: редактор hosts "
                + "из Zapret GUI, антивирус либо вы сами. Показаны, потому что объясняют "
                + "вердикты проверки; удалить можно по одной.";

        // Раскрываем чужих, когда нашлось у них, и возвращаем как было,
        // когда поиск сняли: оставить раскрытыми семьсот строк — значит
        // отдать им весь экран после одного запроса.
        if (searching && foreign.Count > 0)
            ShowForeignPanel(true);
        else if (!searching)
            ShowForeignPanel(false);

        Status.Text = searching
            ? $"Нашлось: {ours.Count} наших и {foreign.Count} чужих."
            : _ours.Count == 0
                ? "Нами ничего не прибито."
                : $"Прибито нами: {_ours.Count}. Файл: {HostsFile.DefaultPath}";
    }

    private void ShowForeignPanel(bool open)
    {
        ForeignPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Chevrons.Turn(ForeignChevron, open);
    }

    /// <summary>
    /// Удаляет чужую строку из файла.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашиваем перед правкой, и это не формальность: отменить нажатием
    /// нельзя, файл общий, а запись могла держать чью-то работу. Копия
    /// кладётся рядом всегда — её путь называется вслух, иначе о ней узнают
    /// только те, кто полез в исходники.
    /// </para>
    /// <para>
    /// По номеру строки: две записи могут совпадать дословно, и удалять
    /// «такую же» значило бы снять не ту.
    /// </para>
    /// </remarks>
    private void OnRemoveForeign(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: int line } || line < 0)
            return;

        if (sender is not FrameworkElement { DataContext: PinRow row })
            return;

        var answer = MessageBox.Show(
            $"Удалить чужую запись «{row.Name} → {row.Detail}» из файла hosts?\n\n"
            + "Её ведёт не программа: это мог быть редактор Zapret GUI, антивирус "
            + "или вы сами. Отменить нажатием будет нельзя.\n\n"
            + "Копия файла ляжет рядом — из неё можно вернуть всё целиком.",
            "NetZapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            var backup = HostsEditor.Remove([line]);
            HostsEditor.FlushDns();

            Reload();
            Status.Text = $"Удалено: {row.Name}. Копия прежнего файла: {backup}";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось удалить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Прибивает имя к адресу, введённым руками.</summary>
    /// <remarks>
    /// Отдельно от окна пина, которое подбирает живой адрес пробами: здесь
    /// адрес уже известен, и подбирать нечего. Проверка адреса строгая —
    /// строка, не разобравшаяся в адрес, ушла бы в файл и осталась там
    /// молча нерабочей.
    /// </remarks>
    private void OnAddPin(object sender, RoutedEventArgs e) => AddPin();

    private void OnNewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            AddPin();
    }

    private void AddPin()
    {
        var name = NewName.Text.Trim().Trim('/').ToLowerInvariant();

        // Из адреса берём только имя: люди вставляют ссылку целиком.
        if (name.Contains("://"))
            name = name.Split("://")[1];

        name = name.Split('/')[0].TrimStart('*', '.');

        if (name.Length == 0 || !name.Contains('.') || name.Contains(' '))
        {
            Status.Text = "Слева нужно имя вида example.com.";
            return;
        }

        if (!System.Net.IPAddress.TryParse(NewAddress.Text.Trim(), out var address))
        {
            Status.Text = "Справа нужен адрес вида 93.184.216.34 — имя там не подойдёт: "
                + "файл hosts разрешает имена в адреса, а не в другие имена.";

            return;
        }

        try
        {
            var result = HostsEditor.Pin(
                new Dictionary<string, string> { [name] = address.ToString() },
                note: "вручную");

            HostsEditor.FlushDns();

            NewName.Clear();
            NewAddress.Clear();

            Reload();

            // Про чужие записи на то же имя говорим сразу: пока они на месте,
            // имя разрешается дважды, и предсказать исход по файлу не выйдет.
            Status.Text = result.Reverted is { } reverted
                ? reverted
                : result.Shadowed.Count > 0
                ? $"Прибито: {name} → {address}. Но на это же имя есть чужие записи "
                  + $"({result.Shadowed.Count}) — снимите их, иначе какая сработает, "
                  + "по файлу не скажешь."
                : $"Прибито: {name} → {address}. Прибито нами всего: {result.Pinned}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось прибить: " + ex.GetBaseException().Message;
        }
    }

    private void OnForeignToggle(object sender, RoutedEventArgs e) =>
        ShowForeignPanel(ForeignPanel.Visibility != Visibility.Visible);

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            // Блокнотом, а не своим редактором: файл системный, и человек,
            // правящий его руками, должен видеть его целиком — вместе
            // с чужими записями, которых мы не показываем.
            Process.Start(new ProcessStartInfo("notepad.exe", HostsFile.DefaultPath)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Стучится в каждый прибитый адрес и говорит, кто молчит.</summary>
    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();

        CheckButton.IsEnabled = false;
        CheckButton.Content = "Проверяю…";

        try
        {
            var pins = HostsEditor.Pins();
            var rows = new List<PinRow>();

            foreach (var (name, addresses) in pins.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var first = addresses.Split(',')[0].Trim();
                bool alive = await AnswersAsync(first, _work.Token);

                rows.Add(new PinRow(
                    name,
                    addresses,
                    alive ? "отвечает" : "молчит",
                    (Brush)FindResource(alive ? "Accent" : "Danger")));

                // В общий список, а не прямо в показ: иначе проверка
                // затирала бы отбор поиска и на каждом ответе возвращала
                // бы все сто строк поверх найденных трёх.
                _ours = rows.ToList();
                Filter();
            }

            int dead = rows.Count(r => r.Note == "молчит");

            Status.Text = dead == 0
                ? $"Все {rows.Count} отвечают."
                : $"Молчат {dead} из {rows.Count}. Такой пин ведёт в никуда и выглядит блокировкой — "
                    + "снимите его или подберите адрес заново.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Проверка прервана.";
        }
        catch (Exception ex)
        {
            Status.Text = "Проверка не удалась: " + ex.GetBaseException().Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
            CheckButton.Content = "Проверить адреса";
        }
    }

    private static async Task<bool> AnswersAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            await client.ConnectAsync(address, 443, timeout.Token);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnUnpin(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;

        // Спрашиваем: файл системный, общий и переживает удаление программы.
        var answer = MessageBox.Show(
            $"Снять пин с {name}?\n\nЗапись уберётся из нашего блока; чужие строки на то же имя, "
            + "если они есть, останутся и снова начнут действовать.",
            "NetZapret",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
            return;

        try
        {
            var result = HostsEditor.Unpin([name]);
            HostsEditor.FlushDns();

            Reload();
            Status.Text = $"Снято. Осталось прибитых: {result.Pinned}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось снять: " + ex.GetBaseException().Message;
        }
    }
}
