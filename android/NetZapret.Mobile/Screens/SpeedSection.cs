using Avalonia.Controls;
using Avalonia.Layout;
using NetZapret.Mobile.Shell;
using NetZapret.Proxy;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Замер скорости» — напрямую, несколькими соединениями.
/// </summary>
/// <remarks>
/// <para>
/// Тот же <see cref="SpeedTest"/>, что у окна Windows: четыре соединения
/// к Cloudflare. Одно соединение упирается в поток, а не в канал (CLAUDE.md:
/// 36 Мбит/с одним curl против 270–286 в четыре).
/// </para>
/// <para>
/// «Через туннель» здесь нет: на Windows он шёл через вход <c>health-in</c>,
/// а на телефоне слушателей на 127.0.0.1 без пароля нет (android/DESIGN.md).
/// Путь замера через выход — решение за владельцем (ui.md, шов S3).
/// </para>
/// <para>
/// Замер идёт напрямую всегда, и при поднятом VPN тоже: пакет приложения
/// выведен из своего VPN (<c>exclude_package</c>, android/DESIGN.md) —
/// через него ходит ByeDPI. До 27.10 здесь и на экране было сказано, что
/// при поднятом VPN это скорость через туннель, — неверно.
/// </para>
/// </remarks>
internal sealed class SpeedSection : Section
{
    private readonly TextBlock _down = Ui.Text("—", "h1");
    private readonly TextBlock _up = Ui.Text("—", "h1");
    private readonly TextBlock _ping = Ui.Text("—", "h1");
    private readonly TextBlock _status = Ui.Text("", "muted", "small");
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly Button _run;
    private CancellationTokenSource? _running;

    public SpeedSection(MainView shell) : base(shell)
    {
        _run = Ui.Button("Начать замер", () => _ = RunAsync(), "primary", "big");
        _run.HorizontalAlignment = HorizontalAlignment.Stretch;

        var meters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8 };
        meters.Children.Add(Meter("↓ Скачивание", _down, "Мбит/с"));
        var up = Meter("↑ Отдача", _up, "Мбит/с");
        Grid.SetColumn(up, 1);
        meters.Children.Add(up);
        var ping = Meter("Задержка", _ping, "мс");
        Grid.SetColumn(ping, 2);
        meters.Children.Add(ping);

        Body.Children.Add(Ui.Card(meters));
        Body.Children.Add(_run);
        Body.Children.Add(Ui.Stack(6, _bar, _status));
        Body.Children.Add(Ui.Card(Ui.Text("Замер качает и отдаёт около 100–200 МБ — на мобильной сети это "
            + "трафик тарифа. Это скорость сети без туннеля, и когда VPN поднят: сам NetZapret из своего "
            + "VPN выведен.", "muted", "small"), "warncard"));
    }

    public override string Title => "Замер скорости";

    protected override void Hidden() => _running?.Cancel();

    private static Control Meter(string caption, TextBlock value, string unit) =>
        Ui.Stack(2, Ui.Text(caption, "muted", "small"), value, Ui.Text(unit, "faint", "small"));

    private async Task RunAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        _running = CancellationTokenSource.CreateLinkedTokenSource(WhileVisible);
        var token = _running.Token;
        _run.Content = new TextBlock { Text = "Остановить", TextAlignment = Avalonia.Media.TextAlignment.Center };
        _bar.IsVisible = true;
        _down.Text = _up.Text = _ping.Text = "—";

        var progress = new Progress<SpeedReading>(reading =>
        {
            _bar.Value = reading.Fraction;
            _status.Text = reading.Phase switch
            {
                SpeedPhase.Connect => "Связь с сервером замера…",
                SpeedPhase.Ping => "Задержка…",
                SpeedPhase.Download => "Скачивание…",
                _ => "Отдача…",
            };

            switch (reading.Phase)
            {
                case SpeedPhase.Ping when reading.PingMs is { } ms:
                    _ping.Text = $"{ms:0}";
                    break;
                case SpeedPhase.Download when reading.Mbps > 0:
                    _down.Text = $"{reading.Mbps:0}";
                    break;
                case SpeedPhase.Upload when reading.Mbps > 0:
                    _up.Text = $"{reading.Mbps:0}";
                    break;
            }
        });

        try
        {
            var result = await Task.Run(() => new SpeedTest().RunAsync(progress, token), token);
            _down.Text = result.DownMbps is { } down ? $"{down:0}" : "—";
            _up.Text = result.UpMbps is { } up ? $"{up:0}" : "—";
            _ping.Text = result.PingMs is { } ping ? $"{ping:0}" : "—";
            _status.Text = result.Problem ?? SpeedTest.Describe(result);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Замер остановлен.";
        }
        catch (Exception ex)
        {
            _status.Text = "Замер не прошёл: " + ex.GetBaseException().Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _bar.IsVisible = false;
            _run.Content = new TextBlock { Text = "Начать замер", TextAlignment = Avalonia.Media.TextAlignment.Center };
        }
    }
}
