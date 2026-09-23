using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Резолвер в списке выбора.</summary>
public sealed class ResolverRow
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Note { get; init; }

    /// <summary>Отклик либо причина молчания; до замера — прочерк.</summary>
    public string Latency { get; set; } = "—";

    public string Key { get; set; } = "Faint";

    public bool Chosen { get; set; }

    public Brush Color => (Brush)Application.Current.FindResource(Key);

    public Brush Edge => (Brush)Application.Current.FindResource(Chosen ? "Accent" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Строка обзора резолверов.</summary>
/// <remarks>
/// Цвета — по смыслу, как на снимке владельца: зелёное прошло, красное
/// перехвачено или подменено, серое не мерилось.
/// </remarks>
public sealed class SurveyRow
{
    private SurveyRow(DnsSurveyRow row) => Source = row;

    public DnsSurveyRow Source { get; }

    public string Name => Source.Provider.Name;

    public string Doh => Ms(Source.DohMs, Source.DohFailure);

    public string Dot => Ms(Source.DotMs, Source.DotFailure);

    public string Udp
    {
        get
        {
            var text = Ms(Source.UdpMs, Source.UdpFailure);

            return Source.UdpMs is not null && Source.UdpAnswered < Source.Provider.Udp.Count
                ? $"{text} {Source.UdpAnswered}/{Source.Provider.Udp.Count}"
                : text;
        }
    }

    public string Real => Source.RealResolver is null
        ? "—"
        : $"{Source.RealResolver} → {Source.RealNetwork ?? "?"}";

    public string Spoof => Source.SpoofChecked == 0 ? "—" : $"{Source.Spoofed}/{Source.SpoofChecked}";

    public Brush DohColor => Paint(Source.DohMs, Source.DohFailure);

    public Brush DotColor => Paint(Source.DotMs, Source.DotFailure);

    public Brush UdpColor => Paint(Source.UdpMs, Source.UdpFailure);

    public Brush RealColor => Brush(Source.RealResolver is null ? "Faint" : Source.Intercepted ? "Danger" : "Accent");

    public Brush SpoofColor => Brush(Source.SpoofChecked == 0 ? "Faint" : Source.Spoofed > 0 ? "Danger" : "Accent");

    public static SurveyRow From(DnsSurveyRow row) => new(row);

    private static string Ms(double? ms, string failure) =>
        ms is { } value ? $"{value:0.0} мс" : failure.Length > 0 ? failure : "—";

    private static Brush Paint(double? ms, string failure) =>
        Brush(ms is not null ? "Accent" : failure.Length > 0 ? "Danger" : "Faint");

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>
/// Апстрим DNS туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Речь только о том резолвере, которого спрашивает sing-box. Настройки DNS
/// самой Windows не трогаются ни здесь, ни где-либо ещё: подмена системного
/// резолвера ломает то, что от него зависит — корпоративные имена, принтеры,
/// сетевые диски, — и переживает удаление программы.
/// </para>
/// <para>
/// Проверяется тем же <see cref="DnsProbe"/>, что и в консоли: настоящим
/// запросом DoH в проводном формате. Перекрывают именно запрос, и проверка
/// пингом отвечала бы на вопрос «жив ли хост» вместо «разрешится ли имя».
/// </para>
/// </remarks>
public partial class DnsView : UserControl
{
    /// <summary>Сколько ждать один запрос.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _work;

    public DnsView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _work?.Cancel();
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowChosen(settings);
        ShowSystem();
        ShowTunnelWarning();

        Resolvers.ItemsSource = DnsProbe.Known
            .Select(r => new ResolverRow
            {
                Name = r.Name,
                Address = r.Address,
                Note = r.Note ?? string.Empty,
                Chosen = string.Equals(r.Address, settings.DnsServer, StringComparison.Ordinal),
            })
            .ToList();

        Status.Text = "Замер не делался. Нажмите «Проверить» — шесть запросов, несколько секунд.";
    }

    private void ShowChosen(AppSettings settings)
    {
        var known = DnsProbe.Known.FirstOrDefault(r =>
            string.Equals(r.Address, settings.DnsServer, StringComparison.Ordinal));

        ChosenName.Text = known is null
            ? settings.DnsServer
            : $"{known.Name} · {known.Address}";

        // Заполняем, не поднимая события выбора: иначе показ состояния
        // тут же записал бы его обратно в настройки и позвал уведомление
        // о перезапуске — при каждом заходе на вкладку.
        _filling = true;
        TunnelMode.SelectedIndex = settings.DnsThroughTunnel ? 1 : 0;
        _filling = false;

        // Обе стороны выбора имеют цену, и названа она честно: включённое
        // прячет запрос от оператора, но ставит разрешение имён в зависимость
        // от туннеля — пока тот не поднялся, не открывается ничего.
        TunnelHint.Text = settings.DnsThroughTunnel
            ? "Запросы идут внутри туннеля: оператору они неотличимы от прочего трафика "
              + "и заблокировать их отдельно нельзя. Цена — пока туннель не поднялся, имена "
              + "не разрешаются вовсе, включая то, что работало на десинке."
            : "Запросы идут напрямую к выбранному резолверу. Если оператор перекроет DoH — "
              + "а в августе 2026 это сделали несколько российских, — включайте «через туннель».";
    }

    /// <summary>Показывает, кого спрашивает сама Windows.</summary>
    private void ShowSystem()
    {
        try
        {
            var found = SystemResolvers.Discover();

            SystemValue.Text = found.Count == 0
                ? "не нашлись"
                : string.Join(", ", found);
        }
        catch (Exception ex)
        {
            SystemValue.Text = "не читаются: " + ex.GetBaseException().Message;
        }
    }

    private void ShowTunnelWarning()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        TunnelCard.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Resolvers.ItemsSource is not IReadOnlyList<ResolverRow> rows)
            return;

        ShowTunnelWarning();

        _work?.Cancel();
        _work = new CancellationTokenSource();

        RunButton.IsEnabled = false;
        Status.Text = "Спрашиваю каждого по имени example.com…";

        foreach (var row in rows)
        {
            row.Latency = "…";
            row.Key = "Faint";
        }

        Redraw();

        try
        {
            var results = await DnsProbe.CheckAllAsync(
                DnsProbe.Known,
                Timeout,
                result => Dispatcher.Invoke(() =>
                {
                    Fill(rows, result);
                    Redraw();
                }),
                _work.Token);

            int working = results.Count(r => r.Works);

            Status.Text = working == 0
                ? "Не ответил ни один. Если туннель поднят — это его правило; если нет — "
                  + "оператор перекрыл DoH, и стоит включить «через туннель»."
                : $"Ответили {working} из {results.Count}. Нажмите на строку, чтобы выбрать.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Замер прерван.";
        }
        catch (Exception ex)
        {
            Status.Text = "Замер сорвался: " + ex.GetBaseException().Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private static void Fill(IReadOnlyList<ResolverRow> rows, DnsProbeResult result)
    {
        var row = rows.FirstOrDefault(r => r.Address == result.Resolver.Address);

        if (row is null)
            return;

        if (result.Works && result.Latency is { } latency)
        {
            row.Latency = $"{latency.TotalMilliseconds:0} мс";

            // Порог не про качество связи, а про ощущение: до полусекунды
            // задержка резолвера теряется в открытии страницы, дальше уже
            // заметна на каждом новом имени.
            row.Key = latency.TotalMilliseconds < 500 ? "Accent" : "Warn";

            return;
        }

        row.Latency = "не отвечает";
        row.Key = "Danger";
    }

    private void Redraw()
    {
        var shown = Resolvers.ItemsSource;

        Resolvers.ItemsSource = null;
        Resolvers.ItemsSource = shown;
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { DnsServer = address };
            settings.Save(AppSettings.DefaultPath);

            ShowChosen(settings);

            if (Resolvers.ItemsSource is IEnumerable<ResolverRow> rows)
            {
                foreach (var row in rows)
                    row.Chosen = row.Address == address;

                Redraw();
            }

            Status.Text = $"Апстрим: {address}. Применится при следующем запуске движков.";
            this.Offer($"Резолвер сменён на {address}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Идёт заполнение списка, а не выбор человека.
    /// </summary>
    /// <remarks>
    /// Список пишет выбор привязкой и поднимает событие при каждом показе
    /// вкладки. Без этой заслонки заход на вкладку записывал бы настройку
    /// обратно и звал уведомление о перезапуске — на ровном месте.
    /// </remarks>
    private bool _filling;

    private void OnThroughTunnel(object sender, RoutedEventArgs e)
    {
        // IsInitialized — от события, которое список поднимает прямо
        // при разборе разметки: соседние элементы к тому мгновению ещё
        // не созданы. На вкладке маршрутов это стоило падения при
        // создании вкладки, и повторять его здесь незачем.
        if (!IsInitialized || _filling || sender is not ComboBox box || box.SelectedIndex < 0)
            return;

        try
        {
            bool through = box.SelectedIndex == 1;
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Выбор того же самого — не выбор.
            if (settings.DnsThroughTunnel == through)
                return;

            var next = settings with { DnsThroughTunnel = through };

            next.Save(AppSettings.DefaultPath);

            ShowChosen(next);

            Status.Text = next.DnsThroughTunnel
                ? "Имена будут разрешаться внутри туннеля. Применится при следующем запуске движков."
                : "Имена будут разрешаться напрямую. Применится при следующем запуске движков.";

            this.Offer(next.DnsThroughTunnel
                ? "DNS переведён внутрь туннеля"
                : "DNS переведён на прямой путь");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Обзор резолверов — кто отвечает на самом деле и подменяет ли.
    /// </summary>
    /// <remarks>
    /// Строки появляются по мере готовности: провайдеров пятнадцать, и таблица,
    /// молчащая до последнего, выглядела бы зависшей.
    /// </remarks>
    private async void OnSurvey(object sender, RoutedEventArgs e)
    {
        SurveyButton.IsEnabled = false;
        SurveyHead.Visibility = Visibility.Visible;

        var rows = new System.Collections.ObjectModel.ObservableCollection<SurveyRow>();
        Survey.ItemsSource = rows;
        SurveyStatus.Text = "Проверяю… Запросы идут мимо туннеля, через адаптер.";

        try
        {
            var progress = new Progress<DnsSurveyRow>(row => rows.Add(SurveyRow.From(row)));
            var all = await DnsSurvey.SurveyAllAsync(progress: progress);

            // Порядок — как в списке провайдеров, а не как пришли ответы.
            Survey.ItemsSource = all.Select(SurveyRow.From).ToList();

            var intercepted = all.Where(r => r.Spoofed > 0).Select(r => r.Provider.Name).ToList();

            SurveyStatus.Text = intercepted.Count == 0
                ? "Подмены по UDP не найдено."
                : $"По UDP подменяют ответы: {string.Join(", ", intercepted)}. "
                  + "Обычный DNS к ним перехвачен по дороге — пользуйтесь DoH или DoT.";
        }
        catch (Exception ex)
        {
            SurveyStatus.Text = "Обзор не удался: " + ex.GetBaseException().Message;
        }
        finally
        {
            SurveyButton.IsEnabled = true;
        }
    }
}
