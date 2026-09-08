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

        TunnelButton.Content = settings.DnsThroughTunnel
            ? "Спрашивать напрямую"
            : "Спрашивать через туннель";

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

    private void OnThroughTunnel(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings with { DnsThroughTunnel = !settings.DnsThroughTunnel };

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
}
