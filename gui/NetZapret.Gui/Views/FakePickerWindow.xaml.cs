using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Строка окна выбора подделки.</summary>
/// <param name="Blob">Что пишется в настройки; <c>null</c> — как в пресете.</param>
public sealed record FakePickItem(string? Blob, string Title, string Note, string Mark);

/// <summary>
/// Выбор подделки для одного места секции пресета (<see cref="FakeChoice"/>).
/// </summary>
/// <remarks>
/// Окном, а не выпадающим списком в карточке: на прозрачном фоне темы список
/// сливался со строками под ним (владелец 03.11). Отказ — «Отмена» и крестик.
/// </remarks>
public partial class FakePickerWindow : Window
{
    private readonly IReadOnlyList<FakePickItem> _all;

    /// <summary>Выбранный образец; <c>null</c> — как в пресете. Смотреть при <c>DialogResult == true</c>.</summary>
    public string? Picked { get; private set; }

    /// <param name="chosen">Выбранный сейчас вместо пресетовского; <c>null</c> — стоит пресетовский.</param>
    public FakePickerWindow(FakeSlot slot, IReadOnlyList<FakeBlob> offer, string? chosen, string? zapretRoot)
    {
        InitializeComponent();

        Head.Text = $"Подделка для «{slot.Section}»";
        Step.Text = (slot.Udp ? "UDP · " : "TCP · ") + slot.Step;

        var items = new List<FakePickItem>
        {
            new(null, $"Как в пресете — {slot.Blob}", "Вернуть то, что стоит в файле пресета.", chosen is null ? "сейчас" : string.Empty),
        };

        foreach (var blob in offer)
            items.Add(new FakePickItem(blob.Id, blob.Title, Describe(blob, zapretRoot), blob.Id == chosen ? "сейчас" : string.Empty));

        _all = items;

        Status.Text = slot.Udp
            ? $"Образцов для UDP: {offer.Count}. Для голоса Discord пробуют stun2 и ACTIVE_DISCORD_UDP (обсуждение №30)."
            : $"Образцов для TCP: {offer.Count} — приветствия TLS разных сайтов.";

        ShowList();

        Loaded += (_, _) => Search.Focus();
    }

    private static string Describe(FakeBlob blob, string? zapretRoot)
    {
        var kind = blob.Kind switch
        {
            FakeKind.Tls => "приветствие TLS",
            FakeKind.Quic => "первый пакет QUIC",
            FakeKind.Udp => "пакет UDP",
            _ => "прочее",
        };

        if (blob.BuiltIn)
            return kind + " · встроен в winws2";

        try
        {
            var size = zapretRoot is null ? 0 : new FileInfo(Path.Combine(zapretRoot, "bin", blob.Id)).Length;
            return size > 0 ? $"{kind} · {size} Б · {blob.Id}" : $"{kind} · {blob.Id}";
        }
        catch (IOException)
        {
            return $"{kind} · {blob.Id}";
        }
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ShowList();

    private void ShowList()
    {
        var text = Search.Text.Trim();

        List.ItemsSource = text.Length == 0
            ? _all
            : _all.Where(i => i.Blob is null || i.Title.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FakePickItem item })
        {
            Picked = item.Blob;
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
