using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetZapret.Core.Services;

namespace NetZapret.Gui.Views;

/// <summary>Что выбрали для точечной проверки.</summary>
/// <param name="Service">Сервис из справочника; <c>null</c> — своё имя.</param>
/// <param name="Host">Своё имя; <c>null</c> — выбран сервис.</param>
internal sealed record CheckTarget(string? Service, string? Host)
{
    public string Describe => Service ?? Host ?? string.Empty;
}

/// <summary>
/// Выбор цели для точечной проверки.
/// </summary>
/// <remarks>
/// Отдельным окном, потому что прежде глубина и сервисы лежали в одном
/// выпадающем списке: две глубины сверху, а под ними два десятка сервисов
/// одной лентой. Выбор глубины и выбор цели — разные вопросы, и слитый
/// список отвечал сразу на оба, заставляя пролистывать чужое.
/// </remarks>
public partial class TargetPicker : Window
{
    internal CheckTarget? Chosen { get; private set; }

    public TargetPicker()
    {
        InitializeComponent();

        Services.ItemsSource = ServiceCatalog.All.Select(s => s.Name).ToList();
    }

    private void OnOwnChanged(object sender, TextChangedEventArgs e)
    {
        // Своё имя и сервис исключают друг друга: набранное вручную снимает
        // выбор в списке, иначе непонятно, что именно поедет в проверку.
        if (Own.Text.Trim().Length > 0)
            Services.SelectedItem = null;

        UpdateAccept();
    }

    private void OnServiceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (Services.SelectedItem is not null)
            Own.Clear();

        UpdateAccept();
    }

    private void OnServiceConfirmed(object sender, MouseButtonEventArgs e)
    {
        if (Services.SelectedItem is not null)
            OnAccept(sender, e);
    }

    private void UpdateAccept() =>
        Accept.IsEnabled = Services.SelectedItem is not null || Own.Text.Trim().Length > 0;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var own = Own.Text.Trim();

        Chosen = Services.SelectedItem is string service
            ? new CheckTarget(service, null)
            : own.Length > 0 ? new CheckTarget(null, own) : null;

        DialogResult = Chosen is not null;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
