using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetZapret.Gui.Views;

/// <summary>
/// Содержимое плитки быстрого действия на «Главной»: значок в квадрате, заголовок и подпись.
/// </summary>
/// <remarks>
/// По макету владельца 19.10. Собирается в коде, а не шаблоном: плиток четыре,
/// и разметка у них одна — разными были бы только три строки.
/// </remarks>
public sealed class HomeAction : Grid
{
    /// <summary>Знак IconFont.</summary>
    public string Glyph { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Чем это поможет — одной строкой.</summary>
    public string Note { get; set; } = string.Empty;

    private TextBlock? _title;
    private TextBlock? _note;
    private StackPanel? _words;

    /// <summary>Размер заголовка, когда он влезает.</summary>
    private const double TitleSize = 13;

    /// <summary>Мельче не ужимается — дальше многоточие.</summary>
    private const double SmallestTitle = 11;

    /// <summary>
    /// Сжатый вид: заголовок в две строки, без подписи.
    /// </summary>
    /// <remarks>
    /// Для узкого окна: плитки остаются в одном ряду. По две в ряд вторая строка
    /// стоила 58 точек, и «Главная» в окне 1067×712 (не развёрнутом, у владельца
    /// 19.10) уходила под прокрутку.
    /// </remarks>
    public bool Compact
    {
        set
        {
            if (_title is null || _note is null)
                return;

            _title.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _title.MaxHeight = value ? 36 : double.PositiveInfinity;
            _note.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            Fit();
        }
    }

    /// <summary>
    /// Заголовок в одну строку ужимается до <see cref="SmallestTitle"/>, а не
    /// обрезается.
    /// </summary>
    /// <remarks>
    /// С шрифтом шире Bahnschrift (Manrope, 20.10, снимок 1536×794)
    /// «Проверить блокировки» и «Открыть конфиг» выходили с многоточием.
    /// Перенос на вторую строку прибавил бы плитке высоты, а «Главная»
    /// держится без прокрутки (см. <see cref="Compact"/>), — поэтому мельче,
    /// а не выше. Подпись под заголовком по-прежнему обрезается: она вторична.
    /// </remarks>
    private void Fit()
    {
        if (_title is null || _words is null)
            return;

        double room = _words.ActualWidth;

        if (_title.TextWrapping == TextWrapping.Wrap || room <= 0 || Title.Length == 0)
        {
            _title.FontSize = TitleSize;
            return;
        }

        // В том же режиме, что рисует окно, и с запасом в пару точек: 20.10
        // расчёт давал 147,8 из 148 — «влезает», а на экране было многоточие.
        var typeface = new Typeface(_title.FontFamily, _title.FontStyle, _title.FontWeight, _title.FontStretch);
        var text = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            TitleSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(_title),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double need = text.WidthIncludingTrailingWhitespace + 2;
        double size = need <= room ? TitleSize : Math.Max(SmallestTitle, Math.Floor(TitleSize * room / need * 2) / 2);

        if (_title.FontSize != size)
            _title.FontSize = size;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Шрифт выбирают в «Оформлении» на ходу, а ширина плитки от этого не меняется.
        if (e.Property == TextElement.FontFamilyProperty && _title is not null)
            Dispatcher.BeginInvoke(Fit, DispatcherPriority.Loaded);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());

        var tile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        tile.SetResourceReference(Border.BackgroundProperty, "Raised");
        tile.SetResourceReference(Border.BorderBrushProperty, "Border");

        var glyph = new TextBlock
        {
            Text = Glyph,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        tile.Child = glyph;

        var title = _title = new TextBlock
        {
            Text = Title,
            FontSize = TitleSize,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var note = _note = new TextBlock
        {
            Text = Note,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var words = _words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.SizeChanged += (_, _) => Fit();
        words.Children.Add(title);
        words.Children.Add(note);
        SetColumn(words, 1);

        Children.Add(tile);
        Children.Add(words);
    }
}
