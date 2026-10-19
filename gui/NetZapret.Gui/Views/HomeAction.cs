using System.Windows;
using System.Windows.Controls;

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
        }
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
            FontSize = 13,
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

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(title);
        words.Children.Add(note);
        SetColumn(words, 1);

        Children.Add(tile);
        Children.Add(words);
    }
}
