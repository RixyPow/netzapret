using System.Windows;
using NetZapret.Subscriptions;

namespace NetZapret.Gui.Views;

/// <summary>
/// Правка подписки: имя и замена ссылки.
/// </summary>
/// <remarks>
/// Отказ — «Отмена» и крестик: ничего не меняется.
/// </remarks>
public partial class SubscriptionEditWindow : Window
{
    private readonly IReadOnlyCollection<string> _taken;

    /// <summary>Новое имя; <c>null</c> — отказались.</summary>
    public string? ChosenName { get; private set; }

    /// <summary>Новая ссылка; <c>null</c> — оставить прежнюю.</summary>
    public string? ChosenUrl { get; private set; }

    /// <param name="taken">Имена других подписок: два одинаковых имени не различить в меню и в метках пула.</param>
    public SubscriptionEditWindow(string name, IReadOnlyCollection<string> taken)
    {
        InitializeComponent();

        _taken = taken;
        NameBox.Text = name;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            Say("Имя не может быть пустым.");
            return;
        }

        if (_taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Say($"Подписка «{name}» уже есть. Дайте другое имя.");
            return;
        }

        var raw = UrlBox.Password.Trim();

        if (raw.Length > 0)
        {
            // Та же проверка, что при добавлении: обёртки клиентов разворачиваются.
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
                || SubscriptionClient.Unwrap(parsed) is not { Scheme: "http" or "https" } unwrapped)
            {
                Say("Это не похоже на ссылку подписки: нужна http, https либо обёртка happ, clash или sn.");
                return;
            }

            ChosenUrl = unwrapped.ToString();
        }

        ChosenName = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Say(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }
}
