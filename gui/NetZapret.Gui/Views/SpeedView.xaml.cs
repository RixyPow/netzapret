using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetZapret.Core.Updates;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Раздел «Замер скорости».
/// </summary>
/// <remarks>
/// <para>
/// Владелец 30.09: «замер скорости прям в программе», «как спидтест»,
/// «на отдельной вкладке». Считает <see cref="SpeedTest"/>, помнит
/// <see cref="SpeedHistory"/>, выводит <see cref="SpeedVerdict"/> — раздел
/// только показывает.
/// </para>
/// <para>
/// Путей два, и в этом весь смысл: «через туннель» идёт через вход проверки
/// движка (тот же, которым надзор проверяет проход трафика), «напрямую» —
/// мимо всякого прокси, так, как идёт трафик, не уведённый в VPN. В день,
/// когда замер появился, разница между ними и нашла главную беду туннеля.
/// </para>
/// <para>
/// Раздел создаётся заново при каждом заходе (MainWindow.OnSection). Итоги
/// лежат в файле истории; кривые последнего замера каждого пути и адрес,
/// с которого нас видели, — в статических полях, до закрытия программы:
/// кривые в файл не идут за ненадобностью, адрес — потому что личный.
/// Уход из раздела замер обрывает: он гонит трафик, а показывать его некому.
/// </para>
/// </remarks>
public partial class SpeedView : UserControl
{
    private static readonly Point Centre = new(150, 112);
    private const double Radius = 100;
    private const double Thickness = 11;

    /// <summary>Сколько прошлых замеров показывать.</summary>
    private const int HistoryShown = 4;

    /// <summary>
    /// Уже этого вывод уходит под таблицу замеров.
    /// </summary>
    /// <remarks>
    /// Считается от таблицы, а не на глаз: её столбцы с цифрами занимают 356
    /// точек, путю нужно не меньше 150 («Через туннель» со значком), поля
    /// карточки и строки — 56, и рядом ещё 312 под вывод. Первый порог, 760,
    /// был взят на глаз: на окне владельца в 765 точек столбцу пути осталось
    /// сорок, и «Через туннель» легло поверх скорости (снимок 30.09).
    /// </remarks>
    private const double NarrowBelow = 356 + 150 + 56 + 312 + 6;

    /// <summary>Последний замер пути со всем, чего нет в истории.</summary>
    private sealed record Shot(
        SpeedEntry Entry, string? Address, List<double> Down, List<double> Up, List<double> Ping);

    private static Shot? _tunnelShot;
    private static Shot? _directShot;

    private readonly Path _value = new();
    private readonly RotateTransform _needle = new(SpeedGauge.StartAngle, Centre.X, Centre.Y);

    private CancellationTokenSource? _run;
    private bool _tunnel;
    private bool _tunnelUp;
    private string? _exit;

    /// <summary>Показания идущего замера — для кривых.</summary>
    private List<double> _down = [];
    private List<double> _up = [];
    private List<double> _ping = [];

    /// <summary>Куда стрелка идёт и где она сейчас, Мбит/с.</summary>
    private double _target;
    private double _shown;

    /// <summary>Стрелка в движении — подписка на кадры стоит.</summary>
    private bool _moving;

    /// <summary>
    /// Раз в несколько секунд смотрим, поднят ли туннель и через что идёт.
    /// </summary>
    /// <remarks>
    /// Прежде это узнавалось один раз, при входе в раздел. Владелец 30.09:
    /// «после замера через туннель/напрямую не меняется» — движки стояли
    /// (только что отработал build.cmd), половинка «Через туннель» была
    /// погашена, и подняв движки, он так бы и видел её погашенной до нового
    /// захода в раздел.
    /// </remarks>
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(4) };

    public SpeedView()
    {
        InitializeComponent();
        DrawDial();

        StreamsText.Text = $"{new SpeedTestOptions().Streams} разом";

        _watch.Tick += async (_, _) =>
        {
            // Во время замера путь не трогаем: он выбран и идёт.
            if (_run is null && await LookAtTunnelAsync() && IsLoaded && _run is null)
                Choose(_tunnel);
        };

        Loaded += async (_, _) =>
        {
            await LookAtTunnelAsync();

            if (!IsLoaded)
                return;

            // Туннель — первым выбором, если есть: за ним сюда и приходят.
            Choose(_tunnelUp);
            ShowHistory();
            _watch.Start();
        };

        Unloaded += (_, _) =>
        {
            _watch.Stop();
            StopFrames();
            _run?.Cancel();
        };
    }

    /// <summary>
    /// Поднят ли туннель и через какой сервер он идёт.
    /// </summary>
    /// <returns>Изменилось ли что-нибудь с прошлого раза.</returns>
    private async Task<bool> LookAtTunnelAsync()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        bool up = state is not null
            && state.IsSupervisorAlive()
            && state.Services.Any(s => s.Name == "sing-box" && s.ProcessId is not null);

        string? exit = up ? (await TunnelStatus.CurrentExitAsync(CancellationToken.None)).Server : null;

        bool changed = up != _tunnelUp || exit != _exit;

        _tunnelUp = up;
        _exit = exit;

        return changed;
    }

    /// <summary>Выбор пути: переключатель, справка и последний замер этого пути.</summary>
    private void Choose(bool tunnel)
    {
        _tunnel = tunnel;

        ViaTunnel.Style = (Style)FindResource(tunnel ? "SegmentOn" : "SegmentOff");
        Direct.Style = (Style)FindResource(tunnel ? "SegmentOff" : "SegmentOn");

        var shot = tunnel ? _tunnelShot : _directShot;

        // Половинки переключателя живые всегда: погашенная «Через туннель»
        // читалась как сломанный переключатель. Нельзя мерить — гаснет кнопка
        // замера, и рядом сказано почему.
        bool possible = !tunnel || _tunnelUp;

        Go.IsEnabled = possible;

        WhatText.Text = !possible
            ? "Движок туннеля не запущен — мерить нечего. Запустите движки на «Главной», и замер станет доступен сам."
            : tunnel
            ? _exit == TunnelBypass.DirectTag
                ? "Скорость через туннель. Сейчас включён обход: выходы не отвечают, и трафик туннеля идёт напрямую — замер покажет прямую сеть."
                : $"Скорость через туннель — так идёт всё, что уведено в VPN. Сейчас через {ExitName(_exit) ?? "сервер, который движок не назвал"}."
            : "Скорость мимо VPN — так идёт всё, что не уведено в туннель, вместе с десинком, если он поднят.";

        ServerText.Text = shot?.Entry.Node is { Length: > 0 } node ? $"Cloudflare, узел {node}" : "Cloudflare, ближайший узел";

        SeenText.Text = shot?.Address is { Length: > 0 } address
            ? address + (shot.Entry.Country is { Length: > 0 } country ? $" ({country})" : string.Empty)
            : "покажет замер";

        SpentText.Text = (shot?.Entry.Bytes > 0
                ? $"в прошлый раз {shot.Entry.Bytes / (1024 * 1024)} МБ"
                : "сотни мегабайт: чем быстрее, тем больше")
            + (tunnel ? " — из трафика подписки" : string.Empty);

        ShowShot(shot);
        _ = ShowQuotaAsync(tunnel && _tunnelUp);
    }

    /// <summary>
    /// Предупреждение о лимите трафика подписки — только для замера через туннель.
    /// </summary>
    /// <remarks>
    /// Считается вне потока окна: ради остатка трафика разбираются запасы
    /// всех подписок в работе. К панелям обращений нет — только запас на диске.
    /// </remarks>
    private async Task ShowQuotaAsync(bool wanted)
    {
        QuotaBox.Visibility = Visibility.Collapsed;

        if (!wanted)
            return;

        var exit = _exit;
        long? spent = _tunnelShot?.Entry.Bytes;

        var warning = await Task.Run(() => QuotaWarning(exit, spent));

        // Пока считали, путь могли сменить.
        if (!IsLoaded || !_tunnel || warning is null)
            return;

        QuotaText.Text = warning;
        QuotaBox.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Слова о лимите у подписки, чей сервер держит туннель; сервер не нашёлся
    /// ни в одной — у всех подписок в работе, у которых лимит есть.
    /// </summary>
    private static string? QuotaWarning(string? exit, long? spent)
    {
        try
        {
            var parts = SubscriptionBook.Load().Pool
                .Select(entry => (entry.Name, Kept: SubscriptionPool.Kept(entry.Url)))
                .Where(part => part.Kept is not null)
                .Select(part => (part.Name, part.Kept!.Value.Info))
                .ToList();

            if (parts.Count == 0)
                return null;

            // Теги — те же, что в конфиге движка: по ним и ищем хозяина сервера.
            var tags = SubscriptionPool.Tag(
                parts.Select(part => (part.Name, part.Info.Servers)).ToList());

            int owner = exit is null ? -1 : tags.ToList().FindIndex(list => list.Contains(exit));

            // Расход — по прошлому замеру через туннель, если он был.
            spent ??= SpeedHistory.Load().FirstOrDefault(e => e.Tunnel && e.Bytes > 0)?.Bytes;

            var warnings = (owner >= 0 ? [parts[owner]] : parts)
                .Select(part => SpeedQuota.Warning(part.Name, part.Info, spent))
                .OfType<string>()
                .ToList();

            return warnings.Count == 0 ? null : string.Join(" ", warnings);
        }
        catch (Exception)
        {
            // Предупреждение — удобство: не вышло его собрать, замеру это не помеха.
            return null;
        }
    }

    private void OnTunnel(object sender, RoutedEventArgs e)
    {
        if (_run is null)
            Choose(true);
    }

    private void OnDirect(object sender, RoutedEventArgs e)
    {
        if (_run is null)
            Choose(false);
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        if (_run is not null)
        {
            _run.Cancel();
            return;
        }

        bool tunnel = _tunnel;

        using var run = new CancellationTokenSource();
        _run = run;

        ShowRunning(true);
        ShowShot(null);

        _down = [];
        _up = [];
        _ping = [];

        var options = new SpeedTestOptions
        {
            Proxy = tunnel
                ? EngineKeys.Loopback(SingBoxOptions.DefaultHealthPort, EngineKeys.Current())
                : null,
        };

        // Progress создан в потоке окна и туда же возвращает показания.
        var progress = new Progress<SpeedReading>(Show);

        string? note = null;

        try
        {
            var result = await Task.Run(() => new SpeedTest(options).RunAsync(progress, run.Token));
            var entry = SpeedEntry.From(result, tunnel, ExitName(_exit), DateTimeOffset.Now);
            var shot = new Shot(entry, result.Address, _down, _up, _ping);

            if (tunnel)
                _tunnelShot = shot;
            else
                _directShot = shot;

            SpeedHistory.Add(entry);

            // В журнал — без адреса: он личный, а журнал уходит в отчёты.
            Journal.Write("замер", $"{(tunnel ? "через туннель" : "напрямую")}: {SpeedTest.Describe(result)}"
                + (result.Country is { } country ? $"; страна выхода {country}, узел {result.Node}" : string.Empty));
        }
        catch (OperationCanceledException)
        {
            note = "Замер остановлен.";
        }
        catch (Exception ex)
        {
            note = "Замер не удался: " + ex.GetBaseException().Message;
        }
        finally
        {
            _run = null;
            Move(0);

            ShowRunning(false);

            // Тот же путь: справка подхватит свежий узел, адрес и расход,
            // блоки — итог с кривыми (либо прошлый замер, если этот оборван).
            Choose(tunnel);
            ShowHistory();

            if (note is not null)
                Status.Text = note;
        }
    }

    private void ShowRunning(bool running)
    {
        GoText.Text = running ? "Остановить" : "Начать замер";
        GoGlyph.Text = running ? "" : "";

        ViaTunnel.IsEnabled = !running;
        Direct.IsEnabled = !running;

        // Идёт замер — кнопка живая: она же «Остановить».
        if (running)
            Go.IsEnabled = true;

        PhaseText.Text = string.Empty;
    }

    /// <summary>Показание на ходу: стрелка, кривая, подпись этапа; итог этапа — в крупные цифры.</summary>
    private void Show(SpeedReading reading)
    {
        if (_run is null)
            return;

        switch (reading.Phase)
        {
            case SpeedPhase.Connect:
                PhaseText.Text = "устанавливается связь…";
                break;

            case SpeedPhase.Ping when reading.Done:
                PingText.Text = reading.PingMs is { } median ? $"{median:0}" : "—";
                break;

            case SpeedPhase.Ping:
                PhaseText.Text = "задержка";

                if (reading.PingMs is { } ms)
                {
                    PingText.Text = $"{ms:0}";
                    _ping.Add(ms);
                    PingLine.Show(_ping);
                }

                break;

            case SpeedPhase.Download when reading.Done:
                DownText.Text = SpeedVerdict.Number(reading.Mbps);
                Move(0);
                break;

            case SpeedPhase.Upload when reading.Done:
                UpText.Text = SpeedVerdict.Number(reading.Mbps);
                Move(0);
                break;

            case SpeedPhase.Download:
                PhaseText.Text = "скачивание";
                DownText.Text = SpeedVerdict.Number(reading.Mbps);
                Move(reading.Mbps);
                _value.SetResourceReference(Shape.StrokeProperty, "Accent");
                _down.Add(reading.Mbps);
                DownLine.Show(_down);
                break;

            case SpeedPhase.Upload:
                PhaseText.Text = "отдача";
                UpText.Text = SpeedVerdict.Number(reading.Mbps);
                Move(reading.Mbps);
                _value.SetResourceReference(Shape.StrokeProperty, "Warn");
                _up.Add(reading.Mbps);
                UpLine.Show(_up);
                break;
        }
    }

    /// <summary>Блоки с цифрами и кривыми — по замеру; <c>null</c> — пусто.</summary>
    private void ShowShot(Shot? shot)
    {
        var entry = shot?.Entry;

        DownText.Text = entry?.DownMbps is { } down ? SpeedVerdict.Number(down) : "—";
        UpText.Text = entry?.UpMbps is { } up ? SpeedVerdict.Number(up) : "—";
        PingText.Text = entry?.PingMs is { } ping ? $"{ping:0}" : "—";

        DownLine.Show(shot?.Down ?? []);
        UpLine.Show(shot?.Up ?? []);
        PingLine.Show(shot?.Ping ?? []);

        DownWord.Text = Steadiness(shot?.Down);
        UpWord.Text = Steadiness(shot?.Up);
        PingWord.Text = entry?.JitterMs is { } jitter ? $"разброс {jitter:0.#} мс" : " ";

        Status.Text = entry?.Problem is { Length: > 0 } problem
            ? char.ToUpper(problem[0]) + problem[1..] + "."
            : string.Empty;

        // Слово о том, как шла скорость: по разбросу показаний после разгона.
        static string Steadiness(List<double>? readings) =>
            readings is null ? " " : SpeedVerdict.Unevenness(readings) switch
            {
                null => " ",
                <= SpeedVerdict.Steady => "стабильно",
                _ => "неровно",
            };
    }

    /// <summary>Таблица прошлых замеров и вывод из последнего.</summary>
    private void ShowHistory()
    {
        var history = SpeedHistory.Load();

        HistoryRows.Children.Clear();
        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < Math.Min(HistoryShown, history.Count); i++)
            HistoryRows.Children.Add(Row(history[i], latest: i == 0));

        if (history.Count == 0)
        {
            VerdictTitle.Text = "Пока нечего сказать";
            VerdictText.Text = "Замерьте оба пути — будет видно, сколько скорости стоит туннель.";
            CopyButton.IsEnabled = false;
            return;
        }

        var (title, text) = SpeedVerdict.Describe(history[0], SpeedVerdict.Pair(history[0], history));

        VerdictTitle.Text = title;
        VerdictText.Text = text;
        CopyButton.IsEnabled = true;
    }

    /// <summary>Строка таблицы замеров; свежая — на подложке.</summary>
    private Border Row(SpeedEntry entry, bool latest)
    {
        var grid = new Grid();

        foreach (var width in new[]
        {
            new GridLength(78), new GridLength(1, GridUnitType.Star),
            new GridLength(104), new GridLength(104), new GridLength(70),
        })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        var at = entry.At.ToLocalTime();

        Add(0, at.Date == DateTime.Today ? $"{at:HH:mm}" : $"{at:dd.MM HH:mm}", muted: true);

        // DockPanel, а не StackPanel: тот отдаёт тексту сколько попросит,
        // и в тесном столбце подпись ложилась поверх соседнего. Здесь текст
        // получает остаток столбца и обрезается многоточием.
        var path = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 8, 0) };

        var glyph = new TextBlock
        {
            Text = entry.Tunnel ? "" : "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        glyph.SetResourceReference(TextBlock.ForegroundProperty, entry.Tunnel ? "Accent" : "Muted");
        path.Children.Add(glyph);

        path.Children.Add(new TextBlock
        {
            Text = (entry.Tunnel ? "Через туннель" : "Напрямую")
                + (entry.DownMbps is null && entry.UpMbps is null ? " — не удался" : string.Empty),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(path, 1);
        grid.Children.Add(path);

        Add(2, entry.DownMbps is { } down ? $"{SpeedVerdict.Number(down)} Мбит/с" : "—");
        Add(3, entry.UpMbps is { } up ? $"{SpeedVerdict.Number(up)} Мбит/с" : "—");
        Add(4, entry.PingMs is { } ping ? $"{ping:0} мс" : "—");

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            ToolTip = Tip(entry),
        };

        if (latest)
            row.SetResourceReference(Border.BackgroundProperty, "Raised");

        return row;

        void Add(int column, string text, bool muted = false)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };

            if (muted)
                block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        static string? Tip(SpeedEntry entry)
        {
            var parts = new List<string>();

            if (entry.Exit is { Length: > 0 } exit)
                parts.Add($"сервер {exit}");

            if (entry.Country is { Length: > 0 } country)
                parts.Add($"страна выхода {country}");

            if (entry.Node is { Length: > 0 } node)
                parts.Add($"узел Cloudflare {node}");

            if (entry.Bytes > 0)
                parts.Add($"ушло {entry.Bytes / (1024 * 1024)} МБ");

            if (entry.Problem is { Length: > 0 } problem)
                parts.Add(problem);

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var history = SpeedHistory.Load();

        if (history.Count == 0)
            return;

        try
        {
            Clipboard.SetText(SpeedVerdict.Copy(history[0], SpeedVerdict.Pair(history[0], history), UpdateCheck.Current));
            CopyText.Text = "Скопировано";
        }
        catch (Exception)
        {
            // Буфер обмена занят другой программой — бывает, и не наша беда.
            CopyText.Text = "Буфер занят — ещё раз";
        }
    }

    /// <summary>На узком окне вывод уходит под таблицу замеров.</summary>
    private void OnPageSize(object sender, SizeChangedEventArgs e)
    {
        bool narrow = Page.ActualWidth < NarrowBelow;

        BottomGap.Width = new GridLength(narrow ? 0 : 12);
        BottomSide.Width = new GridLength(narrow ? 0 : 300);

        Grid.SetColumn(VerdictCard, narrow ? 0 : 2);
        Grid.SetRow(VerdictCard, narrow ? 1 : 0);
        VerdictCard.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
    }

    /// <summary>
    /// Имя сервера без флага: WPF знаки флагов не рисует, и в строке они
    /// выходят двумя буквами («ᴇᴇ Эстония»).
    /// </summary>
    private static string? ExitName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var (_, name) = CountryTag.Split(tag);

        return name.Length > 0 ? name : tag;
    }

    /// <summary>
    /// Дуга, отметки шкалы и стрелка.
    /// </summary>
    private void DrawDial()
    {
        var track = new Path
        {
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Arc(SpeedGauge.StartAngle, SpeedGauge.StartAngle + SpeedGauge.Sweep),
        };

        // Приглушённый текст вполсилы, а не цвет поверхности: в теме со стеклом
        // поверхности прозрачны, и дуга цвета Raised терялась на картинке
        // (снимок на теме владельца, 30.09).
        track.SetResourceReference(Shape.StrokeProperty, "Muted");
        track.Opacity = 0.28;
        Dial.Children.Add(track);

        _value.StrokeThickness = Thickness;
        _value.StrokeStartLineCap = PenLineCap.Round;
        _value.StrokeEndLineCap = PenLineCap.Round;
        _value.SetResourceReference(Shape.StrokeProperty, "Accent");
        Dial.Children.Add(_value);

        for (int i = 0; i < SpeedGauge.Marks.Count; i++)
        {
            double angle = SpeedGauge.StartAngle + SpeedGauge.Sweep * i / (SpeedGauge.Marks.Count - 1);
            var at = SpeedGauge.At(Centre, Radius - 27, angle);

            var label = new TextBlock
            {
                Text = $"{SpeedGauge.Marks[i]:0}",
                Width = 44,
                TextAlignment = TextAlignment.Center,
                Style = (Style)FindResource("Caption"),
            };

            Canvas.SetLeft(label, at.X - 22);
            Canvas.SetTop(label, at.Y - 8);
            Dial.Children.Add(label);
        }

        var needle = new Polygon
        {
            Points =
            [
                new Point(Centre.X, Centre.Y - (Radius - 42)),
                new Point(Centre.X - 3.5, Centre.Y),
                new Point(Centre.X + 3.5, Centre.Y),
            ],
            RenderTransform = _needle,
            Opacity = 0.9,
        };

        needle.SetResourceReference(Shape.FillProperty, "Text");
        Dial.Children.Add(needle);

        var hub = new Ellipse { Width = 13, Height = 13 };

        hub.SetResourceReference(Shape.FillProperty, "Text");
        Canvas.SetLeft(hub, Centre.X - 6.5);
        Canvas.SetTop(hub, Centre.Y - 6.5);
        Dial.Children.Add(hub);
    }

    /// <summary>Дуга шкалы от угла до угла; пустая — ничего.</summary>
    private static Geometry Arc(double from, double to)
    {
        if (to - from < 0.5)
            return Geometry.Empty;

        var figure = new PathFigure { StartPoint = SpeedGauge.At(Centre, Radius, from), IsClosed = false };

        figure.Segments.Add(new ArcSegment
        {
            Point = SpeedGauge.At(Centre, Radius, to),
            Size = new Size(Radius, Radius),
            IsLargeArc = to - from > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        return new PathGeometry([figure]);
    }

    /// <summary>
    /// Велит стрелке идти к показанию.
    /// </summary>
    /// <remarks>
    /// Подписка на кадры — только пока стрелка идёт. Первая версия держала
    /// её всё время, пока раздел открыт, и окно из-за этого отрисовывалось
    /// без остановки: сторож подвисаний у владельца 30.09 писал «поток окна
    /// был занят 300–700 мс, раздел «Замер скорости»» каждые несколько секунд,
    /// при нуле процессорного времени, — и на простое, без всякого замера.
    /// </remarks>
    private void Move(double target)
    {
        _target = target;

        if (_moving || _target == _shown)
            return;

        _moving = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_moving)
            return;

        _moving = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>
    /// Стрелка догоняет показание: показания приходят раз в 150 мс, и без
    /// этого она шла бы рывками.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        double next = Motion.Enabled ? _shown + (_target - _shown) * 0.16 : _target;

        if (Math.Abs(next - _target) < 0.05)
            next = _target;

        _shown = next;

        double angle = SpeedGauge.Angle(_shown);

        _needle.Angle = angle;
        _value.Data = Arc(SpeedGauge.StartAngle, angle);

        // Дошла — кадры больше не нужны.
        if (_shown == _target)
            StopFrames();
    }
}
