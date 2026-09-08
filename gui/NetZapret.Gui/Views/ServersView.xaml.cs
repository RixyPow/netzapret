using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Gui.Views;

/// <summary>Строка одного сервера подписки.</summary>
public sealed record ServerRow(
    string Tag,
    string Detail,
    string Latency,
    Brush Color,
    string ChooseLabel,
    bool CanChoose);

/// <summary>
/// Серверы подписки: что есть, что живо, чем идём.
/// </summary>
/// <remarks>
/// <para>
/// Замеры хранятся в том же <see cref="ServerHealthCache"/>, который читает
/// консоль. Проверок было две, а помнила программа одну — прогон сообщал,
/// что работают четыре, а выбор сервера показывал прежние цифры. Здесь тот же
/// кэш, и окно с меню не разойдутся.
/// </para>
/// <para>
/// Ссылка на подписку не показывается и не пишется в журнал: она равнозначна
/// паролю, по ней выдаются адреса серверов и ключи.
/// </para>
/// </remarks>
public partial class ServersView : UserControl
{
    private readonly ServerHealthCache _health = ServerHealthCache.Load();
    private IReadOnlyList<ProxyServer> _servers = [];
    private CancellationTokenSource? _work;

    public ServersView()
    {
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _work?.Cancel();
    }

    private async Task LoadAsync()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
        {
            Status.Text = "Подписка не задана. Без неё серверов нет, а VPN недоступен — "
                + "десинк при этом работает.";

            return;
        }

        Status.Text = "Читаю подписку…";
        RefreshButton.IsEnabled = false;

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), CancellationToken.None);

            _servers = info.Servers.Where(s => s.IsSupportedBySingBox).ToList();

            ShowQuota(info);
            Show(settings);

            // Отброшенные называются числом, а не замалчиваются: человек,
            // видящий в подписке двадцать серверов и пятнадцать здесь,
            // вправе знать, куда делись пять.
            int skipped = info.Servers.Count - _servers.Count;

            Status.Text = skipped == 0
                ? $"Серверов: {_servers.Count}."
                : $"Серверов: {_servers.Count}. Ещё {skipped} sing-box не поддерживает.";
        }
        catch (Exception ex)
        {
            Status.Text = "Подписка не прочиталась: " + ex.GetBaseException().Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowQuota(SubscriptionInfo info)
    {
        QuotaCard.Visibility = Visibility.Visible;

        QuotaValue.Text = info.RemainingBytes is { } left
            ? Size(left)
            : info.TotalBytes > 0 ? "кончилась" : "без ограничения";

        ExpiryValue.Text = info.ExpiresAt is { } until
            ? $"{(until - DateTimeOffset.Now).Days} дн"
            : "без срока";

        CountValue.Text = info.Servers.Count.ToString();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} ТБ",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        _ => $"{bytes / 1024.0:0.#} КБ",
    };

    private void Show(AppSettings settings)
    {
        Servers.ItemsSource = _servers.Select(server =>
        {
            var known = _health.Find(server.Tag);
            bool chosen = server.Tag == settings.PreferredServer;

            var (latency, key) = known switch
            {
                { Success: true, LatencyMs: { } ms } => ($"{ms:0} мс", "Accent"),
                { Success: true } => ("отвечает", "Accent"),
                { Success: false } => ("не отвечает", "Danger"),
                _ => ("не замерян", "Faint"),
            };

            var detail = $"{server.Protocol}, {server.Host}:{server.Port}";

            if (known is not null)
                detail += $" · замер {Ago(known.CheckedAt)}";

            return new ServerRow(
                server.Tag,
                detail,
                latency,
                (Brush)FindResource(chosen ? "Accent" : key),
                chosen ? "выбран" : "выбрать",
                !chosen);
        }).ToList();
    }

    private static string Ago(DateTimeOffset when)
    {
        var passed = DateTimeOffset.Now - when;

        return passed switch
        {
            { TotalMinutes: < 1 } => "только что",
            { TotalHours: < 1 } => $"{(int)passed.TotalMinutes} мин назад",
            { TotalDays: < 1 } => $"{(int)passed.TotalHours} ч назад",
            _ => $"{(int)passed.TotalDays} дн назад",
        };
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    /// <summary>
    /// Гоняет пробу по всем серверам и запоминает результат.
    /// </summary>
    /// <remarks>
    /// Через локальный вход, без TUN: работающий обход при этом не прерывается,
    /// и права администратора не нужны — хотя у окна они и так есть.
    /// </remarks>
    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (_servers.Count == 0)
            return;

        var singBox = FindSingBox();

        if (singBox is null)
        {
            Status.Text = "Движок sing-box не найден рядом с программой — замерять нечем.";
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        MeasureButton.IsEnabled = false;
        MeasureButton.Content = "Измеряю…";

        int done = 0;
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        try
        {
            await new ProxyProbe(singBox).RunManyAsync(
                _servers,
                new ProbeOptions
                {
                    // Внешний адрес здесь не показывают, а его поиск стоит
                    // секунд на каждом сервере.
                    LookupExternalIp = false,
                    LogLevel = "warn",
                },
                result =>
                {
                    _health.Set(new ServerHealth
                    {
                        Tag = result.ServerTag,
                        Success = result.Success,
                        LatencyMs = result.Latency?.TotalMilliseconds,
                        CheckedAt = DateTimeOffset.Now,
                    });

                    // Обновляем на каждом ответе: проверка идёт полминуты,
                    // и таблица, оживающая на глазах, куда честнее полосы
                    // загрузки, которая ничего не измеряет.
                    Dispatcher.Invoke(() =>
                    {
                        done++;
                        Status.Text = $"Измерено {done} из {_servers.Count}…";
                        Show(settings);
                    });
                },
                _work.Token);

            _health.Save();

            int alive = _servers.Count(s => _health.Find(s.Tag) is { Success: true });
            Status.Text = $"Отвечают {alive} из {_servers.Count}. Замер сохранён — меню увидит те же цифры.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Замер прерван.";
        }
        catch (Exception ex)
        {
            Status.Text = "Замер не удался: " + ex.GetBaseException().Message;
        }
        finally
        {
            MeasureButton.IsEnabled = true;
            MeasureButton.Content = "Замерить все";
        }
    }

    /// <summary>
    /// Записывает выбор и говорит, что он вступит в силу перезапуском.
    /// </summary>
    /// <remarks>
    /// Не перезапускаем сами. Движки несут весь трафик машины, и уронить их
    /// в ответ на клик по строке в списке — не та цена, на которую человек
    /// соглашался, выбирая сервер.
    /// </remarks>
    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { PreferredServer = tag };
            settings.Save(AppSettings.DefaultPath);

            Show(settings);
            Status.Text = $"Выбран {tag}. Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Где лежит движок; <c>null</c> — не нашли.</summary>
    private static string? FindSingBox()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

        return File.Exists(beside) ? beside : null;
    }
}
