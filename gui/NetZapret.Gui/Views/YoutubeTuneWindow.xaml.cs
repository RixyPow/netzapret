using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Автонастройка YouTube отдельным окном (14.11): шаги «Замер → Решение → Проверка»
/// и таблица шага — узлы, решения, было и стало.
/// </summary>
public partial class YoutubeTuneWindow : Window
{
    private static readonly string[] Steps = ["Замер", "Решение", "Проверка"];

    private VideoNodesReport? _before;
    private YoutubeTune? _tune;
    private CancellationTokenSource? _work;

    /// <summary>Настройку записали или сняли — «Десинку» обновить карточку.</summary>
    public event Action? Changed;

    public YoutubeTuneWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => OnMeasure(this, new RoutedEventArgs());
        Closed += (_, _) => _work?.Cancel();
    }

    /// <summary>Полоса шагов: до <paramref name="current"/> — пройдены.</summary>
    private void ShowSteps(int current)
    {
        StepBar.Children.Clear();

        for (int i = 0; i < Steps.Length; i++)
        {
            bool done = i < current, now = i == current;
            var brush = now ? Res("Accent") : done ? Res("Text") : Res("Faint");

            var pill = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1.5),
                BorderBrush = now ? Res("Accent") : Res("Border"),
                Background = now ? Res("Raised") : Brushes.Transparent,
                Padding = new Thickness(14, 6, 16, 6),
                Child = new TextBlock
                {
                    Text = (done ? "✓  " : $"{i + 1}  ") + Steps[i],
                    FontWeight = FontWeights.SemiBold,
                    Foreground = brush,
                },
            };

            StepBar.Children.Add(pill);

            if (i < Steps.Length - 1)
            {
                StepBar.Children.Add(new Border
                {
                    Width = 28, Height = 1.5, Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center, Background = Res("Border"),
                });
            }
        }
    }

    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;

        ShowSteps(0);
        Apply.IsEnabled = false;
        Remeasure.IsEnabled = false;
        Body.Children.Clear();
        Status.Text = "Замеряю десять зарубежных узлов видео и кэш провайдера: пинг, TCP и QUIC через обход, ответ мимо обхода. Около 20 секунд…";

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var (report, decision, why) = await Task.Run(() => VideoNodesRun.TuneAsync(settings, token), token);

            if (report is not null)
                Body.Children.Add(NodesTable(report));

            if (decision?.Tune is not { } tune || report is null)
            {
                Status.Text = why ?? decision?.Reasons.FirstOrDefault() ?? "Судить не по чему.";
                return;
            }

            _before = report;
            _tune = tune;

            ShowSteps(1);
            Body.Children.Insert(0, Head("Что поменять"));
            Body.Children.Insert(1, ChoicesTable(decision.Choices));
            Body.Children.Insert(2, Head("Узлы", top: 22));

            Status.Text = decision.NothingLeft
                ? "Десинк для YouTube сделал, что мог: узлы не открываются и с рецептом. Дальше — «Маршруты», YouTube через VPN."
                : tune.Active
                    ? $"Итог: {YoutubeAddon.Describe(tune)}. «Применить» — запишет, перезапустит движки и перемерит."
                    : "Десинку для узлов видео ничего не нужно. «Применить» запишет это — пресет останется как есть.";

            Apply.Content = tune.Active ? "Применить" : "Записать";
            Apply.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status.Text = "Замер не вышел: " + ex.GetBaseException().Message;
        }
        finally
        {
            Remeasure.IsEnabled = true;
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_before is not { } before || _tune is not { } tune)
            return;

        var token = (_work ??= new CancellationTokenSource()).Token;
        var previous = AppSettings.Load(AppSettings.DefaultPath).Youtube;

        Apply.IsEnabled = false;
        Remeasure.IsEnabled = false;
        ShowSteps(2);

        try
        {
            VideoNodesRun.Save(tune);
            Changed?.Invoke();

            Status.Text = "Записано. Перезапускаю движки…";
            var restarted = await EngineControl.RestartAsync("автонастройка YouTube", token);

            if (!restarted.Ok)
            {
                Status.Text = "Движки не перезапустились: " + restarted.Message + " Настройка записана — подействует со следующего запуска.";
                return;
            }

            Status.Text = "Перемеряю узлы, около 20 секунд…";
            var (after, _) = await Task.Run(() => VideoNodesRun.MeasureAsync(AppSettings.Load(AppSettings.DefaultPath), token), token);

            if (after is null)
            {
                Status.Text = "Перезамер не вышел — настройка остаётся.";
                return;
            }

            var (worse, _) = VideoNodesRun.Compare(before, after);

            Body.Children.Clear();
            Body.Children.Add(Head("Было и стало"));
            Body.Children.Add(CompareTable(before, after));
            Body.Children.Add(Head("Узлы после применения", top: 22));
            Body.Children.Add(NodesTable(after));

            if (!worse)
            {
                ShowSteps(3);
                Status.Text = "Готово: стало не хуже — настройка остаётся.";
                CloseButton.Content = "Готово";
                return;
            }

            VideoNodesRun.Save(previous);
            Changed?.Invoke();
            Status.Text = "Стало хуже — возвращаю прежнее и перезапускаю движки…";
            var back = await EngineControl.RestartAsync("автонастройка YouTube: откат", token);
            Status.Text = back.Ok
                ? "Стало хуже — прежнее возвращено."
                : "Стало хуже — прежнее записано, но движки не перезапустились: " + back.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status.Text = "Не вышло: " + ex.GetBaseException().Message;
        }
        finally
        {
            Remeasure.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Head(string text, double top = 0) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 15,
        Margin = new Thickness(2, top, 0, 8),
    };

    /// <summary>Таблица: заголовок бледным, строки через черту; ширины колонок — звёздочками.</summary>
    private static Grid Table(double[] widths, string[] header, IEnumerable<(string Text, string? Brush)[]> rows)
    {
        var grid = new Grid();

        foreach (var w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w, GridUnitType.Star) : GridLength.Auto });

        int row = 0;

        void Line(IReadOnlyList<(string Text, string? Brush)> cells, bool head)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (!head)
            {
                var rule = new Border { BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 1, 0, 0) };
                Grid.SetRow(rule, row);
                Grid.SetColumnSpan(rule, widths.Length);
                grid.Children.Add(rule);
            }

            for (int c = 0; c < cells.Count; c++)
            {
                var cell = new TextBlock
                {
                    Text = cells[c].Text,
                    TextWrapping = c == cells.Count - 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(2, head ? 0 : 8, 12, head ? 6 : 8),
                    Foreground = head ? Res("Muted") : cells[c].Brush is { } b ? Res(b) : Res("Text"),
                    FontSize = head ? 12 : 13,
                    FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal,
                    ToolTip = head ? null : cells[c].Text,
                };

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            row++;
        }

        Line(header.Select(h => (h, (string?)null)).ToArray(), head: true);

        foreach (var cells in rows)
            Line(cells, head: false);

        return grid;
    }

    private static Grid ChoicesTable(IReadOnlyList<YoutubeChoice> choices) => Table(
        [0, 0, 1],
        ["Что", "Решение", "Почему"],
        choices.Select(c => new (string, string?)[] { (c.What, null), (c.Verdict, c.On ? "Accent" : "Muted"), (c.Why, "Muted") }));

    private static string Share(int ok, int tries) => tries == 0 ? "—" : $"{ok} из {tries}";

    private static string? Shade(int ok, int tries) => tries == 0 ? "Muted" : ok == tries ? null : ok == 0 ? "Danger" : "Warn";

    private static string Filter(NodeFilter filter) => filter switch
    {
        NodeFilter.Open => "отвечает любому имени",
        NodeFilter.GoogleOnly => "только имени Google",
        NodeFilter.Closed => "не отвечает никому",
        _ => "—",
    };

    private static Grid NodesTable(VideoNodesReport report)
    {
        var rows = new List<(string, string?)[]>();

        if (report.CacheNode is { } cache)
        {
            rows.Add(
            [
                (cache.GoogleSite ? $"{cache.Short.Replace("rr1---", string.Empty)} · площадка Google" : $"{cache.Short.Replace("rr1---", string.Empty)} · кэш провайдера", cache.GoogleSite ? "Warn" : "Accent"),
                (cache.PingMs is { } p ? $"{p} мс" : "—", null),
                (Share(cache.Tcp, cache.Tries), Shade(cache.Tcp, cache.Tries)),
                (Share(cache.Quic, cache.QuicTries), Shade(cache.Quic, cache.QuicTries)),
                ("—", "Muted"),
                ("—", "Muted"),
                (cache.GoogleSite ? "своего кэша нет: всё видео — с зарубежных узлов" : "с него идёт популярное", "Muted"),
            ]);
        }

        foreach (var node in report.Nodes)
        {
            if (!node.Measured)
            {
                rows.Add([(node.Short, "Muted"), ("—", "Muted"), ("—", "Muted"), ("—", "Muted"), ("—", "Muted"), ("—", "Muted"), (node.Describe(), "Muted")]);
                continue;
            }

            rows.Add(
            [
                (node.Short.Replace("rr1---", string.Empty), null),
                (node.PingMs is { } p ? $"{p} мс" : "—", node.PingMs is null ? "Danger" : null),
                (Share(node.Tcp, node.Tries), Shade(node.Tcp, node.Tries)),
                (Share(node.Quic, node.QuicTries), Shade(node.Quic, node.QuicTries)),
                (node.Lagged > 0 ? $"{node.Lagged}" : "—", node.Lagged > 0 ? "Accent" : "Muted"),
                (node.CeilingMbit is { } m ? $"~{m:0} Мбит/с" : "—", "Muted"),
                (Filter(node.Filter), node.Filter == NodeFilter.Closed ? "Danger" : node.Filter == NodeFilter.Open ? null : "Muted"),
            ]);
        }

        return Table([0, 0, 0, 0, 0, 0, 1],
            ["Узел", "Пинг", "TCP", "QUIC", "Малое окно", "Потолок 64 КБ", "Мимо обхода"], rows);
    }

    private static Grid CompareTable(VideoNodesReport before, VideoNodesReport after)
    {
        int lagBefore = before.Measured.Sum(n => n.Lagged), lagAfter = after.Measured.Sum(n => n.Lagged);

        return Table([0, 0, 0, 1], ["", "Было", "Стало", ""],
        [
            [("TCP через обход", null), (Share(before.Tcp, before.Tries), null), (Share(after.Tcp, after.Tries), after.Tcp < before.Tcp - 4 ? "Danger" : after.Tcp >= before.Tcp ? "Accent" : null), ("соединения к узлам видео", "Muted")],
            [("QUIC", null), (Share(before.Quic, before.QuicTries), null), (Share(after.Quic, after.QuicTries), null), ("ноль после «выбросить QUIC» — так и задумано", "Muted")],
            [("С малым окном", null), ($"{lagBefore}", null), ($"{lagAfter}", null), ("ответы на 0,2 с позже обычного: рецепт включил малое окно", "Muted")],
        ]);
    }
}
