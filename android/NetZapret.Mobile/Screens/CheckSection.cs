using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Mobile.Shell;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Проверка блокировок»: что закрыто в этой сети и чем лечится.
/// </summary>
/// <remarks>
/// <para>
/// Пробы — те же, что у окна Windows (<see cref="BlockCheck.CheckAsync"/>),
/// по тем же целям: одно имя с части сервиса в быстрой, два — в полной, свои
/// маршруты следом (CheckView.Targets). По восемь разом — как окно с 19.10:
/// время съедают закрытые, каждое ждёт ответа секунды.
/// </para>
/// <para>
/// Проверка не обрывается уходом на другую вкладку — идёт минуты, и человек
/// успевает заглянуть на «Главную». Прерывает её только «Прервать».
/// </para>
/// </remarks>
internal sealed class CheckSection : Section
{
    private const int Parallelism = 8;

    private readonly Button _run;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly TextBlock _status = Ui.Text("", "muted", "small");
    private readonly Border _banner;
    private readonly StackPanel _summary = new() { Spacing = 8 };
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly List<TargetReport> _reports = [];
    private CancellationTokenSource? _running;
    private bool _deep;
    private bool _showOk;
    private string? _reportPath;

    public CheckSection(MainView shell) : base(shell)
    {
        _run = Ui.Button("Проверить", () => _ = RunAsync(), "primary", "big");
        _run.HorizontalAlignment = HorizontalAlignment.Stretch;

        _banner = Ui.Card(Ui.Text("Движки работают — видно сеть уже с обходом. Чтобы увидеть, что закрыто "
            + "без NetZapret, остановите их на «Главной».", "small"), "warncard");

        Body.Children.Add(Ui.Text("Что закрыто в этой сети и чем это лечится. Проверка идёт минуты: "
            + "по каждому имени соединение, и каждое ждёт ответа.", "muted"));
        Body.Children.Add(Ui.Segmented(["Быстрая", "Полная"], 0, index => _deep = index == 1));
        Body.Children.Add(_banner);
        Body.Children.Add(_run);
        Body.Children.Add(Ui.Stack(6, _bar, _status));
        Body.Children.Add(_summary);
        Body.Children.Add(_results);

        Shell.EngineChanged += ShowBanner;
    }

    public override string Title => "Проверка блокировок";

    protected override void Shown() => ShowBanner();

    private void ShowBanner() => _banner.IsVisible = Host.Status.IsRunning;

    private async Task RunAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            _status.Text = "Прерываю…";
            return;
        }

        _running = new CancellationTokenSource();
        var token = _running.Token;
        _reports.Clear();
        _reportPath = null;
        _results.Children.Clear();
        _summary.Children.Clear();
        SetRunning(true);
        _status.Text = "Собираю список имён…";

        bool tunnel = Host.Status.IsRunning;

        try
        {
            var (targets, engine) = await Task.Run(() => Prepare(_deep), token);
            _bar.Maximum = Math.Max(1, targets.Count);
            _bar.Value = 0;
            _status.Text = $"Проверено 0 из {targets.Count}…";

            using var slots = new SemaphoreSlim(Parallelism);
            int done = 0;

            var running = targets.Select(async target =>
            {
                await slots.WaitAsync(token);

                try
                {
                    // Имя, заведённое в туннель, проверяется как туннельное: вердикты
                    // про DPI к нему неприменимы (BlockCheck.CheckAsync, throughTunnel).
                    bool tunnelled = tunnel && Proxied(engine, target.Host);
                    var report = await Task.Run(() => BlockCheck.CheckAsync(target.Host, target.Service, token, tunnelled), token);

                    Dispatcher.UIThread.Post(() =>
                    {
                        done++;
                        _bar.Value = done;
                        _status.Text = $"Проверено {done} из {targets.Count}…";

                        // Маркеры зон без адреса — не строки итога (CheckView.RunAsync).
                        if (report.Kind == BlockKind.NoAddress && !OwnLists.IsOwnTarget(target.Service))
                            return;

                        _reports.Add(report);
                        ShowResults();
                    });
                }
                finally
                {
                    slots.Release();
                }
            });

            try
            {
                await Task.WhenAll(running);
            }
            catch (OperationCanceledException)
            {
                // Прерывание — штатный исход: что успели, то и покажем.
            }

            // Последние Post'ы проб — до итога.
            await Dispatcher.UIThread.InvokeAsync(() => { });

            _status.Text = token.IsCancellationRequested
                ? $"Прервано: проверено {_reports.Count}."
                : $"Готово: проверено {_reports.Count}.";

            ShowResults();
            _reportPath = await Task.Run(SaveReport);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Прервано.";
        }
        catch (Exception ex)
        {
            _status.Text = "Проверка не прошла: " + ex.GetBaseException().Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetRunning(false);
            ShowResults();
        }
    }

    private void SetRunning(bool running)
    {
        _run.Content = new TextBlock { Text = running ? "Прервать" : "Проверить", TextAlignment = TextAlignment.Center };
        _run.Classes.Set("primary", !running);
        _run.Classes.Set("stop", running);
        _bar.IsVisible = running;
    }

    private static (IReadOnlyList<(string Host, string Service)> Targets, RuleEngine? Engine) Prepare(bool deep)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var zapretRoot = ZapretPaths.Discover()?.Root;
        RuleEngine? engine = null;

        try
        {
            engine = RuleSetLoader.LoadFor(settings);
            RuleSetExpander.Expand(engine.RuleSet, zapretRoot);
        }
        catch (Exception)
        {
            // Без правил проверка всё равно идёт — только без пометки «через туннель».
        }

        return (Targets(zapretRoot, deep), engine);
    }

    /// <summary>Цели проверки — как у окна Windows (CheckView.Targets).</summary>
    /// <remarks>
    /// Перенесено сюда, пока цели не переехали в библиотеку (ui.md §4 п. 5):
    /// там им и место, чтобы окно и телефон проверяли одно и то же.
    /// </remarks>
    internal static IReadOnlyList<(string Host, string Service)> Targets(string? zapretRoot, bool deep)
    {
        int perPart = deep ? 2 : 1;
        var targets = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in ServiceCatalog.All)
        {
            foreach (var part in service.Parts)
            {
                // Адресные части проверять нечем, реестр — шум из мёртвых доменов.
                if (part.ByAddress || part.Bulk)
                    continue;

                int taken = 0;

                foreach (var domain in HostListReader.Read(part.List, zapretRoot, out _))
                {
                    if (taken == perPart)
                        break;

                    var host = domain.TrimStart('*', '.');

                    // Пропущенное имя расходует свой слот — иначе втянется
                    // такое же бесполезное глубже по списку.
                    if (NotWorthChecking.Contains(host))
                    {
                        taken++;
                        continue;
                    }

                    if (!seen.Add(host))
                        continue;

                    targets.Add((host, $"{service.Name} · {part.Name}"));
                    taken++;
                }
            }
        }

        try
        {
            foreach (var (host, label) in OwnLists.CheckTargets(UserRulesFile.Load(), perPart))
            {
                if (!NotWorthChecking.Contains(host) && seen.Add(host))
                    targets.Add((host, label));
            }
        }
        catch (Exception)
        {
            // Свои правила не прочитались — проверка справочника от этого не теряет смысла.
        }

        return targets;
    }

    private static bool Proxied(RuleEngine? engine, string host) =>
        engine is not null && engine.Evaluate(new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = null,
            RemotePort = 443,
            Hostname = host,
        }).Mode == RoutingMode.Proxy;

    private void ShowResults()
    {
        _summary.Children.Clear();
        _results.Children.Clear();

        if (_reports.Count == 0)
            return;

        var bad = _reports.Where(r => r.Kind != BlockKind.None).OrderByDescending(r => r.Actionable).ThenBy(r => r.Service).ToList();
        var good = _reports.Where(r => r.Kind == BlockKind.None).OrderBy(r => r.Service).ToList();

        var head = Ui.Stack(6,
            Ui.Caption("Итог"),
            Ui.Text($"Доступно {good.Count} из {_reports.Count}", "h2"));

        foreach (var group in bad.Where(r => r.Actionable).GroupBy(r => r.Describe()))
        {
            var sample = group.First();
            head.Children.Add(Ui.Text($"{Capital(group.Key)} — {group.Count()}: {sample.Remedy()}", "small"));
        }

        if (_running is null && _reportPath is not null)
        {
            var share = Ui.Button("Поделиться отчётом", () => _ = Host.ShareFileAsync(_reportPath, "text/plain"), "link");
            share.HorizontalAlignment = HorizontalAlignment.Left;
            head.Children.Add(share);
        }

        _summary.Children.Add(Ui.Card(head));

        foreach (var report in bad)
            _results.Children.Add(ReportCard(report));

        if (good.Count > 0)
        {
            var toggle = Ui.Button(_showOk ? $"Скрыть доступные ({good.Count})" : $"Показать доступные ({good.Count})", () =>
            {
                _showOk = !_showOk;
                ShowResults();
            }, "link");

            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            _results.Children.Add(toggle);

            if (_showOk)
            {
                var list = Ui.Stack(4);

                foreach (var report in good)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
                    row.Children.Add(Ui.Dot(Ui.Brush("Accent"), 8));
                    var text = Ui.Stack(0, Ui.Text(report.Host), Ui.Text(report.Service, "muted", "small"));
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    list.Children.Add(row);
                }

                _results.Children.Add(Ui.Card(list));
            }
        }
    }

    private static Control ReportCard(TargetReport report)
    {
        var colour = report.Kind switch
        {
            BlockKind.Handshake or BlockKind.NoAddress => "Muted",
            BlockKind.TlsDpi or BlockKind.Stall or BlockKind.Dns => "Warn",
            _ => "Danger",
        };

        var verdict = Ui.Text(Capital(report.Describe()), "h3");
        verdict.Foreground = Ui.Brush(colour);

        var lines = Ui.Stack(4,
            Ui.Text(report.Host, "h3"),
            report.Service is null ? null : Ui.Text(report.Service, "muted", "small"),
            verdict,
            report.Why is null ? null : Ui.Text(report.Why, "muted", "small"),
            Ui.Text("Лечится: " + report.Remedy(), "small"),
            report.Tunnelled ? Ui.Text("Шло через туннель.", "faint", "small") : null);

        var card = Ui.Card(lines);
        card.BorderBrush = Ui.Brush(colour);
        return card;
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    /// <summary>Отчёт текстом в reports/ — чтобы поделиться им из приложения.</summary>
    private string? SaveReport()
    {
        try
        {
            Directory.CreateDirectory("reports");
            var path = Path.GetFullPath(Path.Combine("reports", $"check-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
            var text = new StringBuilder();
            text.AppendLine($"NetZapret — проверка блокировок, {DateTime.Now:dd.MM.yyyy HH:mm}");
            text.AppendLine(Host.Status.IsRunning ? "Движки работали — сеть с обходом." : "Движки стояли — сеть как есть.");
            text.AppendLine();

            foreach (var report in _reports.OrderBy(r => r.Kind == BlockKind.None).ThenBy(r => r.Service))
            {
                text.AppendLine($"{report.Host}\t{report.Service}\t{report.Describe()}\t"
                    + $"TCP: {report.Tcp.Describe()}\tTLS: {report.DescribeTls()}\tданные: {report.DescribeData()}");
            }

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── Для снимков окна просмотра ────────────────────────────────────────

    /// <summary>Выдуманные итоги — посмотреть раскладку карточек без сети.</summary>
    internal void ShowSample()
    {
        static ProbeOutcome Ok() => new() { Ok = true, Elapsed = TimeSpan.FromMilliseconds(80) };

        static ProbeOutcome Silent() => new() { Ok = false, Detail = "нет ответа за 4 с", Elapsed = TimeSpan.FromSeconds(4) };

        TargetReport Make(string host, string service, BlockKind kind) => new()
        {
            Host = host,
            Service = service,
            Tcp = kind is BlockKind.Full or BlockKind.HttpsPort ? Silent() : Ok(),
            Tls = kind is BlockKind.TlsDpi ? Silent() : Ok(),
            Http = Ok(),
            Data = kind is BlockKind.Stall ? new ProbeOutcome { Ok = false, Started = true, Detail = "замерло на 16 384 Б" } : Ok(),
            Kind = kind,
        };

        _reports.Clear();
        _reports.AddRange(
        [
            Make("www.youtube.com", "YouTube · Сайт", BlockKind.TlsDpi),
            Make("discord.com", "Discord · Сайт и вход", BlockKind.TlsDpi),
            Make("rutracker.org", "RuTracker · Сайт", BlockKind.Full),
            Make("twitchcon.com", "Twitch · Сайт", BlockKind.Stall),
            Make("claude.ai", "Claude · Сайт", BlockKind.GeoBlock),
            Make("github.com", "GitHub · Сайт", BlockKind.None),
            Make("vk.com", "VK · Сайт", BlockKind.None),
            Make("ya.ru", "Яндекс · Сайт", BlockKind.None),
        ]);

        _status.Text = "Готово: проверено 8 (пример).";
        ShowResults();
    }
}
