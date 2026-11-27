using System.IO;
using System.Windows;
using NetZapret.Subscriptions;

namespace NetZapret.Gui.Views;

/// <summary>
/// Правка подписки: имя и замена ссылки либо файла локальной подписки.
/// </summary>
/// <remarks>
/// Отказ — «Отмена» и крестик: ничего не меняется.
/// </remarks>
public partial class SubscriptionEditWindow : Window
{
    private readonly IReadOnlyCollection<string> _taken;
    private readonly bool _isLocal;

    /// <summary>Новое имя; <c>null</c> — отказались.</summary>
    public string? ChosenName { get; private set; }

    /// <summary>Новая ссылка; <c>null</c> — оставить прежнюю.</summary>
    public string? ChosenUrl { get; private set; }

    /// <summary>Новое тело локальной подписки; <c>null</c> — оставить прежнее.</summary>
    public string? ChosenBody { get; private set; }

    /// <param name="taken">Имена других подписок: два одинаковых имени не различить в меню и в метках пула.</param>
    /// <param name="isLocal">Локальная подписка из файла: вместо ссылки предлагается выбрать новый файл.</param>
    public SubscriptionEditWindow(string name, IReadOnlyCollection<string> taken, bool isLocal = false)
    {
        InitializeComponent();

        _taken = taken;
        _isLocal = isLocal;
        NameBox.Text = name;

        if (isLocal)
        {
            UrlSection.Visibility = Visibility.Collapsed;
            FileSection.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Новый файл подписки",
            Filter = "Файлы подписок|*.yaml;*.yml;*.json;*.txt|Все файлы|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            if (new FileInfo(dialog.FileName).Length > LocalSubscriptions.MaxFileBytes)
            {
                Say("Файл слишком большой для подписки.");
                return;
            }

            var text = File.ReadAllText(dialog.FileName);
            var (servers, errors) = SubscriptionParser.ParseBody(text);

            if (servers.Count == 0)
            {
                Say("Файл не разбирается: " + (errors.FirstOrDefault() ?? "серверов нет") + ".");
                return;
            }

            ChosenBody = text;
            Problem.Visibility = Visibility.Collapsed;
            FilePicked.Text = $"Выбран файл «{Path.GetFileName(dialog.FileName)}»: серверов {servers.Count}. Нажмите «Сохранить».";
            FilePicked.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Say("Файл не читается: " + ex.GetBaseException().Message);
        }
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

        if (!_isLocal)
        {
            var raw = UrlBox.Password.Trim();

            if (raw.Length > 0)
            {
                if (SubscriptionClient.IsEncryptedHapp(raw))
                {
                    Say(SubscriptionClient.EncryptedHappMessage);
                    return;
                }

                // Та же проверка, что при добавлении: обёртки клиентов разворачиваются.
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
                    || SubscriptionClient.Unwrap(parsed) is not { Scheme: "http" or "https" } unwrapped)
                {
                    Say("Это не похоже на ссылку подписки: нужна http, https либо обёртка happ, clash или sn.");
                    return;
                }

                ChosenUrl = unwrapped.ToString();
            }
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
