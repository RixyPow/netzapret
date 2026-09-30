using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Раздел «Замер скорости».
/// </summary>
/// <remarks>
/// <para>
/// Владелец 30.09: «замер скорости прям в программе», «как спидтест»,
/// «на отдельной вкладке». Считает <see cref="SpeedTest"/> — раздел только
/// показывает: крупные цифры, дуга со стрелкой, одна кнопка.
/// </para>
/// <para>
/// Путей два, и в этом весь смысл: «через туннель» идёт через вход проверки
/// движка (тот же, которым надзор проверяет проход трафика), «напрямую» —
/// мимо всякого прокси, так, как идёт трафик, не уведённый в VPN. В день,
/// когда замер появился, разница между ними и нашла главную беду туннеля.
/// </para>
/// <para>
/// Раздел создаётся заново при каждом заходе (MainWindow.OnSection), поэтому
/// итоги лежат в статических полях — по одному на путь, до закрытия программы:
/// сравнивают обычно два замера, снятых подряд. Уход из раздела замер
/// обрывает: он гонит трафик, а показывать его некому.
/// </para>
/// </remarks>
public partial class SpeedView : UserControl
{
    private static readonly Point Centre = new(160, 150);
    private const double Radius = 120;
    private const double Thickness = 12;

    private static (SpeedResult Result, DateTime At, string? Exit)? _lastTunnel;
    private static (SpeedResult Result, DateTime At, string? Exit)? _lastDirect;

    private readonly Path _value = new();
    private readonly RotateTransform _needle = new(SpeedGauge.StartAngle, Centre.X, Centre.Y);

    private CancellationTokenSource? _run;
    private bool _tunnel;
    private bool _tunnelUp;
    private string? _exit;

    /// <summary>Куда стрелка идёт и где она сейчас, Мбит/с.</summary>
    private double _target;
    private double _shown;

    public SpeedView()
    {
        InitializeComponent();
        DrawDial();

        Loaded += async (_, _) =>
        {
            CompositionTarget.Rendering += OnFrame;
            await ShowPathsAsync();
        };

        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            _run?.Cancel();
        };
    }

    /// <summary>
    /// Что доступно и что выбрано: туннель — только при поднятом движке.
    /// </summary>
    private async Task ShowPathsAsync()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        _tunnelUp = state is not null
            && state.IsSupervisorAlive()
            && state.Services.Any(s => s.Name == "sing-box" && s.ProcessId is not null);

        if (_tunnelUp)
            (_exit, _) = await TunnelStatus.CurrentExitAsync(CancellationToken.None);

        if (!IsLoaded)
            return;

        ViaTunnel.IsEnabled = _tunnelUp;

        TunnelLine.Text = !_tunnelUp
            ? "Движок туннеля не запущен — мерить нечего."
            : _exit == TunnelBypass.DirectTag
                ? "Сейчас включён обход: трафик туннеля идёт напрямую."
                : $"Сейчас через {ExitName(_exit) ?? "сервер, который движок не назвал"}. Тратит трафик подписки — сотни мегабайт за замер.";

        DirectLine.Text = "Мимо VPN — так идёт всё, что не уведено в туннель.";

        // Туннель — первым выбором, если есть: за ним сюда и приходят.
        Choose(_tunnelUp);
        ShowLast();
    }

    private void Choose(bool tunnel)
    {
        _tunnel = tunnel;

        ViaTunnel.SetResourceReference(BorderBrushProperty, tunnel ? "Accent" : "Border");
        Direct.SetResourceReference(BorderBrushProperty, tunnel ? "Border" : "Accent");
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

        Go.Content = "Остановить";
        ViaTunnel.IsEnabled = false;
        Direct.IsEnabled = false;
        Status.Text = string.Empty;
        DownText.Text = UpText.Text = PingText.Text = "—";
        JitterText.Text = "мс";

        var options = new SpeedTestOptions
        {
            Proxy = tunnel
                ? EngineKeys.Loopback(SingBoxOptions.DefaultHealthPort, EngineKeys.Current())
                : null,
        };

        // Progress создан в потоке окна и туда же возвращает показания.
        var progress = new Progress<SpeedReading>(Show);

        try
        {
            var result = await Task.Run(() => new SpeedTest(options).RunAsync(progress, run.Token));

            if (tunnel)
                _lastTunnel = (result, DateTime.Now, _exit);
            else
                _lastDirect = (result, DateTime.Now, null);

            // В журнал — без адреса: он личный, а журнал уходит в отчёты.
            Journal.Write("замер", $"{(tunnel ? "через туннель" : "напрямую")}: {SpeedTest.Describe(result)}"
                + (result.Country is { } country ? $"; страна выхода {country}, узел {result.Node}" : string.Empty));

            ShowResult(result, tunnel);
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Замер остановлен.";
        }
        catch (Exception ex)
        {
            Status.Text = "Замер не удался: " + ex.GetBaseException().Message;
        }
        finally
        {
            _run = null;
            _target = 0;

            NowText.Text = string.Empty;
            PhaseText.Text = string.Empty;
            Go.Content = "Начать";
            ViaTunnel.IsEnabled = _tunnelUp;
            Direct.IsEnabled = true;

            ShowLast();
        }
    }

    /// <summary>Показание на ходу: стрелка, подпись этапа, итог этапа — в крупные цифры.</summary>
    private void Show(SpeedReading reading)
    {
        if (_run is null)
            return;

        switch (reading.Phase)
        {
            case SpeedPhase.Connect:
                PhaseText.Text = "устанавливается связь…";
                break;

            case SpeedPhase.Ping:
                PhaseText.Text = "задержка";

                if (reading.PingMs is { } ms)
                    PingText.Text = $"{ms:0}";

                break;

            case SpeedPhase.Download when reading.Done:
                DownText.Text = Speed(reading.Mbps);
                _target = 0;
                break;

            case SpeedPhase.Upload when reading.Done:
                UpText.Text = Speed(reading.Mbps);
                _target = 0;
                break;

            case SpeedPhase.Download or SpeedPhase.Upload:
                PhaseText.Text = reading.Phase == SpeedPhase.Download ? "скачивание, Мбит/с" : "отдача, Мбит/с";
                NowText.Text = Speed(reading.Mbps);
                _target = reading.Mbps;
                _value.SetResourceReference(Shape.StrokeProperty, reading.Phase == SpeedPhase.Download ? "Accent" : "Warn");
                break;
        }
    }

    private void ShowResult(SpeedResult result, bool tunnel)
    {
        DownText.Text = result.DownMbps is { } down ? Speed(down) : "—";
        UpText.Text = result.UpMbps is { } up ? Speed(up) : "—";
        PingText.Text = result.PingMs is { } ping ? $"{ping:0}" : "—";
        JitterText.Text = result.JitterMs is { } jitter ? $"мс · разброс {jitter:0.#}" : "мс";

        var lines = new List<string>();

        if (result.Problem is { Length: > 0 } problem)
            lines.Add(char.ToUpper(problem[0]) + problem[1..] + ".");

        if (result.Address is { } address)
        {
            lines.Add($"Виден как {address}"
                + (result.Country is { } country ? $" ({country})" : string.Empty)
                + (result.Node is { } node ? $", узел Cloudflare {node}." : "."));
        }

        long spent = (result.DownBytes + result.UpBytes) / (1024 * 1024);

        if (spent > 0)
            lines.Add($"На замер ушло {spent} МБ" + (tunnel ? " трафика подписки." : "."));

        Status.Text = string.Join("\n", lines);
    }

    /// <summary>Последние замеры обоих путей — чтобы сравнить, не записывая.</summary>
    private void ShowLast()
    {
        TunnelResult.Text = Last(_lastTunnel);
        DirectResult.Text = Last(_lastDirect);

        static string Last((SpeedResult Result, DateTime At, string? Exit)? last)
        {
            if (last is not { } entry)
                return "ещё не мерили";

            var text = $"{entry.At:HH:mm} — {SpeedTest.Describe(entry.Result)}";

            return ExitName(entry.Exit) is { } exit ? $"{text} · {exit}" : text;
        }
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

    /// <summary>До сотни — с десятой, дальше целым: «8,4», «138».</summary>
    private static string Speed(double mbps) => mbps < 100 ? $"{mbps:0.0}" : $"{mbps:0}";

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

        track.SetResourceReference(Shape.StrokeProperty, "Raised");
        Dial.Children.Add(track);

        _value.StrokeThickness = Thickness;
        _value.StrokeStartLineCap = PenLineCap.Round;
        _value.StrokeEndLineCap = PenLineCap.Round;
        _value.SetResourceReference(Shape.StrokeProperty, "Accent");
        Dial.Children.Add(_value);

        for (int i = 0; i < SpeedGauge.Marks.Count; i++)
        {
            double angle = SpeedGauge.StartAngle + SpeedGauge.Sweep * i / (SpeedGauge.Marks.Count - 1);
            var at = SpeedGauge.At(Centre, Radius - 30, angle);

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
                new Point(Centre.X, Centre.Y - (Radius - 46)),
                new Point(Centre.X - 4, Centre.Y),
                new Point(Centre.X + 4, Centre.Y),
            ],
            RenderTransform = _needle,
            Opacity = 0.9,
        };

        needle.SetResourceReference(Shape.FillProperty, "Text");
        Dial.Children.Add(needle);

        var hub = new Ellipse { Width = 14, Height = 14 };

        hub.SetResourceReference(Shape.FillProperty, "Text");
        Canvas.SetLeft(hub, Centre.X - 7);
        Canvas.SetTop(hub, Centre.Y - 7);
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
    /// Стрелка догоняет показание: показания приходят раз в 150 мс, и без
    /// этого она шла бы рывками.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        double next = Motion.Enabled ? _shown + (_target - _shown) * 0.16 : _target;

        if (Math.Abs(next - _target) < 0.05)
            next = _target;

        if (next == _shown)
            return;

        _shown = next;

        double angle = SpeedGauge.Angle(_shown);

        _needle.Angle = angle;
        _value.Data = Arc(SpeedGauge.StartAngle, angle);
    }
}
