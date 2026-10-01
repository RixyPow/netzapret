using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Subscriptions;

namespace NetZapret.Gui.Views;

/// <summary>Что добавлять: подписку или ключи.</summary>
public enum AddKind
{
    Subscription,
    Keys,
}

/// <summary>Выбор в окне добавления.</summary>
/// <param name="Name">Имя подписки; <c>null</c> — придумать. У ключей не бывает.</param>
/// <param name="Input">Ссылка подписки (обёртки развёрнуты) либо ключи строками.</param>
/// <param name="InPool">Сразу в работу.</param>
public sealed record AddRequest(AddKind Kind, string? Name, string Input, bool InPool);

/// <summary>
/// Добавление подписки или ключей — отдельным окном, как в Happ.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 01.10: «сделаем добавление подписки отдельным окном, где всё
/// красиво скомпонуем, как в Happ, а «из файла» и «из буфера» — двумя
/// кнопками сверху». До того это была карточка на вкладке с одним полем
/// на всё.
/// </para>
/// <para>
/// Окно только собирает и проверяет. Добавляет вкладка — тем же кодом,
/// что и прежде, — а окно лишь не отпускает того, что не добавится:
/// ошибка видна здесь же, пока человек ещё может её поправить.
/// </para>
/// </remarks>
public partial class SubscriptionAddWindow : Window
{
    private readonly IReadOnlyCollection<string> _taken;

    /// <summary>Что выбрано; <c>null</c> — отказались.</summary>
    public AddRequest? Request { get; private set; }

    /// <param name="taken">Имена подписок: двух одинаковых не различить в меню и в метках пула.</param>
    /// <param name="pasteOnOpen">Ctrl+V на вкладке: сразу взять из буфера.</param>
    public SubscriptionAddWindow(IReadOnlyCollection<string> taken, bool pasteOnOpen = false)
    {
        InitializeComponent();

        _taken = taken;

        Loaded += (_, _) =>
        {
            if (pasteOnOpen)
                FromClipboard();
            else
                UrlBox.Focus();
        };
    }

    private bool KeysChosen => KindBox.SelectedIndex == 1;

    private void OnKind(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Вызывается и при разборе разметки, раньше, чем поля созданы.
        if (SubscriptionFields is null || KeyFields is null)
            return;

        SubscriptionFields.Visibility = KeysChosen ? Visibility.Collapsed : Visibility.Visible;
        KeyFields.Visibility = KeysChosen ? Visibility.Visible : Visibility.Collapsed;
        Problem.Visibility = Visibility.Collapsed;
    }

    private void OnInPool(object sender, RoutedEventArgs e) =>
        InPoolWord.Text = InPool.IsChecked == true ? "вкл." : "выкл.";

    private void OnFromClipboard(object sender, RoutedEventArgs e) => FromClipboard();

    private void FromClipboard()
    {
        string? text;

        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                text = ReadQr(image);

                if (text is null)
                {
                    Say("В буфере картинка, но QR-кода на ней не нашлось.");
                    return;
                }
            }
            else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
            {
                FromFile(files[0]!);
                return;
            }
            else
            {
                Say("В буфере ничего: скопируйте ключ, ссылку подписки или картинку с QR-кодом.");
                return;
            }
        }
        catch (Exception ex)
        {
            Say("Буфер не читается: " + ex.GetBaseException().Message);
            return;
        }

        Fill(text, name: null, where: "В буфере");
    }

    private void OnFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Ключ из файла",
            Filter = "Ключ или QR-код|*.conf;*.txt;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Все файлы|*.*",
        };

        if (dialog.ShowDialog(this) == true)
            FromFile(dialog.FileName);
    }

    private void FromFile(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();

            if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();

                var text = ReadQr(image);

                if (text is null)
                {
                    Say("На картинке QR-кода не нашлось.");
                    return;
                }

                Fill(text, name: null, where: "В QR-коде");
                return;
            }

            // Ключ или файл .conf — это килобайты. Больше — не тот файл,
            // и читать его целиком в память незачем.
            if (new FileInfo(path).Length > 1_000_000)
            {
                Say("Файл слишком большой для ключа.");
                return;
            }

            Fill(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), "В файле");
        }
        catch (Exception ex)
        {
            Say("Файл не читается: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Найденное — в поля, тип — по найденному. Добавляет только «Добавить».
    /// </summary>
    /// <remarks>
    /// Не добавлять сразу — нарочно: у подписки ещё нет имени, а человек
    /// мог взять не то из буфера. Найденное названо словами, но не показано:
    /// ключ и ссылка равносильны паролю.
    /// </remarks>
    private void Fill(string? text, string? name, string where)
    {
        var found = KeyImport.FromText(text, name);

        if (found.Keys.Count > 0)
        {
            KindBox.SelectedIndex = 1;
            KeysBox.Password = string.Join(" ", found.Keys);
            Show($"{where}: {Count(found.Keys.Count, "ключ", "ключа", "ключей")}. Нажмите «Добавить».");
            return;
        }

        if (found.SubscriptionUrl is { } url)
        {
            KindBox.SelectedIndex = 0;
            UrlBox.Password = url;

            if (NameBox.Text.Trim().Length == 0 && name is not null)
                NameBox.Text = name;

            Show($"{where}: ссылка подписки. Спрашиваю у панели название…");
            _ = LookupAsync(url);
            return;
        }

        Say($"{where} {found.Problem}.");
    }

    /// <summary>Ссылка, о которой уже спрашивали, — чтобы не спрашивать дважды.</summary>
    private string? _askedUrl;

    /// <summary>Имя, подставленное по ответу панели: его можно заменить, а вписанное руками — нет.</summary>
    private string? _autoName;

    private CancellationTokenSource? _lookup;

    /// <summary>Ссылку вписали руками — спросить панель, как и о найденной в буфере.</summary>
    private void OnUrlLeft(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (KeyImport.FromText(UrlBox.Password).SubscriptionUrl is { } url && url != _askedUrl)
            _ = LookupAsync(url);
    }

    /// <summary>
    /// Спрашивает панель о подписке: название — в имя, число серверов — в строку.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Владелец 01.10: «из буфера не может автоматически взять название
    /// подписки?» Может: панели присылают его заголовком profile-title,
    /// и программа его уже читает (SubscriptionInfo.Title) — для окна
    /// добавления его просто не спрашивали.
    /// </para>
    /// <para>
    /// Имя ставится, только если человек не вписал своё. Отказ панели — не
    /// запрет добавить: панели лежат минутами, а программа у добавленной
    /// подписки переспросит сама. Чтение ничего не пишет на диск, так что
    /// «Отмена» не оставляет ответа панели — а в нём ключи.
    /// </para>
    /// </remarks>
    private async Task LookupAsync(string url)
    {
        _lookup?.Cancel();
        var cancel = _lookup = new CancellationTokenSource();
        _askedUrl = url;

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(url), cancel.Token);

            if (cancel.IsCancellationRequested)
                return;

            var title = info.Title?.Trim();
            var current = NameBox.Text.Trim();

            if (!string.IsNullOrEmpty(title) && (current.Length == 0 || current == _autoName))
            {
                _autoName = Unique(title);
                NameBox.Text = _autoName;
            }

            int usable = info.Servers.Count(s => s.IsUsableOutbound);

            Show((string.IsNullOrEmpty(title) ? "Подписка отвечает" : $"Подписка «{title}»")
                + $": серверов {usable}. Нажмите «Добавить».");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Спросили о другой ссылке или закрыли окно — ответ уже не нужен.
        }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested)
                Say("Панель не ответила: " + PanelError.Describe(ex) + ". Добавить всё равно можно — программа переспросит сама.");
        }
    }

    /// <summary>Название панели, если оно ещё не занято; иначе с номером.</summary>
    private string Unique(string title)
    {
        if (!_taken.Contains(title, StringComparer.OrdinalIgnoreCase))
            return title;

        for (int n = 2; ; n++)
        {
            var candidate = $"{title} {n}";

            if (!_taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _lookup?.Cancel();
        base.OnClosed(e);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        bool inPool = InPool.IsChecked == true;

        // Ключи, вставленные в поле ссылки, — тоже ключи: прежде одно поле
        // принимало и то и другое, и люди так и будут вставлять.
        var input = KeysChosen ? KeysBox.Password : UrlBox.Password;
        var found = KeyImport.FromText(input);

        if (found.Keys.Count > 0)
        {
            var (parsed, errors) = KeyRing.Parse(found.Keys);

            if (parsed.Count == 0)
            {
                Say("Ключ не разбирается: " + errors[0]);
                return;
            }

            Request = new AddRequest(AddKind.Keys, null, string.Join("\n", found.Keys), inPool);
            DialogResult = true;
            return;
        }

        if (KeysChosen)
        {
            Say("Ключей не нашлось: нужны строки вида vless://, trojan://, hysteria2://, tuic://, "
                + "wireguard:// и подобные.");
            return;
        }

        if (found.SubscriptionUrl is not { } url)
        {
            Say("Это не похоже на ссылку подписки: нужна http, https либо обёртка happ, clash или sn.");
            return;
        }

        var name = NameBox.Text.Trim();

        if (name.Length > 0 && _taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Say($"Подписка «{name}» уже есть. Дайте другое имя.");
            return;
        }

        Request = new AddRequest(AddKind.Subscription, name.Length == 0 ? null : name, url, inPool);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Show(string text)
    {
        Problem.Visibility = Visibility.Collapsed;
        Found.Text = text;
        Found.Visibility = Visibility.Visible;
    }

    private void Say(string text)
    {
        Found.Visibility = Visibility.Collapsed;
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    /// <summary>QR-код с картинки WPF: пиксели в BGRA — и в библиотеку.</summary>
    private static string? ReadQr(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];

        bgra.CopyPixels(pixels, stride, 0);

        return KeyImport.ReadQr(pixels, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>«1 ключ, 2 ключа, 5 ключей».</summary>
    private static string Count(int n, string one, string few, string many)
    {
        int tens = n % 100;
        int last = n % 10;

        var word = tens is >= 11 and <= 14 ? many
            : last == 1 ? one
            : last is >= 2 and <= 4 ? few
            : many;

        return $"{n} {word}";
    }
}
