using System.Diagnostics;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>Прибитое имя.</summary>
public sealed record PinRow(string Name, string Detail, string Note, Brush Color);

/// <summary>
/// Файл hosts: что прибито и живо ли оно.
/// </summary>
/// <remarks>
/// <para>
/// Показываем только свой блок. Файл ведёт не одна программа — там бывают
/// записи Zapret GUI и человека, — и снимать чужое мы не вправе: кнопка
/// «почистить» в общем системном файле однажды сотрёт то, на чём всё
/// держалось.
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

            Pins.ItemsSource = pins
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new PinRow(
                    p.Key,
                    p.Value,
                    "не проверен",
                    (Brush)FindResource("Faint")))
                .ToList();

            Status.Text = pins.Count == 0
                ? "Нами ничего не прибито. Чужие записи в файле, если они есть, мы не трогаем."
                : $"Прибито нами: {pins.Count}. Файл: {HostsFile.DefaultPath}";
        }
        catch (Exception ex)
        {
            Status.Text = "Файл не читается: " + ex.GetBaseException().Message;
        }
    }

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

                Pins.ItemsSource = rows.ToList();
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
