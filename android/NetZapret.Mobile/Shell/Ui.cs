using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;

namespace NetZapret.Mobile.Shell;

/// <summary>
/// Короткие конструкторы элементов разделов.
/// </summary>
/// <remarks>
/// Разделы собраны кодом, а не разметкой: экраны телефона — списки карточек,
/// и в коде их сборка короче и проверяется компилятором целиком, вместе с
/// данными библиотек. Вид задают классы стилей (Theme/Styles.axaml).
/// </remarks>
internal static class Ui
{
    public static TextBlock Text(string? text, params string[] classes)
    {
        var block = new TextBlock { Text = text ?? string.Empty, TextWrapping = TextWrapping.Wrap };

        foreach (var name in classes)
            block.Classes.Add(name);

        return block;
    }

    public static TextBlock Caption(string text) => Text(text.ToUpperInvariant(), "caption");

    public static StackPanel Stack(double spacing, params Control?[] children)
    {
        var panel = new StackPanel { Spacing = spacing };

        foreach (var child in children)
        {
            if (child is not null)
                panel.Children.Add(child);
        }

        return panel;
    }

    public static StackPanel Row(double spacing, params Control?[] children)
    {
        var panel = Stack(spacing, children);
        panel.Orientation = Orientation.Horizontal;
        return panel;
    }

    public static Border Card(Control child, string kind = "card") =>
        new() { Child = child, Classes = { kind } };

    public static Button Button(string text, Action onClick, params string[] classes)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        };

        foreach (var name in classes)
            button.Classes.Add(name);

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Строка-кнопка: заголовок, пояснение и стрелка — для списков «Ещё» и настроек.</summary>
    public static Button NavRow(string title, string? hint, Action onClick, Geometry? icon = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };

        if (icon is not null)
        {
            var glyph = Icon(icon, 22);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(glyph);
        }

        var texts = Stack(2, Text(title, "h3"), hint is null ? null : Text(hint, "muted", "small"));
        texts.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var arrow = Icon(Icons.Chevron, 16);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        arrow.Opacity = 0.6;
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);

        var button = new Button { Content = grid, MinHeight = 56 };
        button.Classes.Add("row");
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Path Icon(Geometry geometry, double size, IBrush? brush = null)
    {
        var path = new Path
        {
            Data = geometry,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = brush ?? Brush("Text"),
        };

        return path;
    }

    /// <summary>Подпись-пилюля: страна у сервера, путь у маршрута.</summary>
    public static Border Chip(string text, IBrush? foreground = null, IBrush? border = null)
    {
        var block = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold };

        if (foreground is not null)
            block.Foreground = foreground;

        var chip = new Border { Child = block, Classes = { "chip" } };

        if (border is not null)
            chip.BorderBrush = border;

        return chip;
    }

    /// <summary>Подпись слева и значение справа, значение переносится.</summary>
    public static Grid Pair(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var name = Text(label, "muted");
        name.MinWidth = 92;
        grid.Children.Add(name);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    public static Grid Pair(string label, string value) => Pair(label, Text(value));

    public static IBrush Brush(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;

    /// <summary>Выключатель с подписью слева и пояснением под ней.</summary>
    public static Grid Switch(string title, string? hint, bool value, Action<bool> changed)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var texts = Stack(2, Text(title), hint is null ? null : Text(hint, "muted", "small"));
        texts.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(texts);

        var toggle = new ToggleSwitch
        {
            IsChecked = value,
            OnContent = null,
            OffContent = null,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
        };

        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>Выбор одного из нескольких — кнопки в ряд поровну.</summary>
    public static Border Segmented(IReadOnlyList<string> names, int selected, Action<int> chosen)
    {
        var grid = new Grid { ColumnSpacing = 4 };
        var buttons = new List<Button>();

        for (int i = 0; i < names.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            int index = i;

            // Одной строкой, а не переносом: сегмент в два этажа на узком
            // экране читается как два разных выбора. Не влезло — многоточие.
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = names[i],
                    FontSize = 13,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Classes = { "segment" },
            };

            if (i == selected)
                button.Classes.Add("selected");

            button.Click += (_, _) =>
            {
                foreach (var other in buttons)
                    other.Classes.Remove("selected");

                button.Classes.Add("selected");
                chosen(index);
            };

            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            buttons.Add(button);
        }

        return new Border
        {
            Child = grid,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            Background = Brush("Backdrop"),
            BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(1),
        };
    }

    public static Ellipse Dot(IBrush brush, double size = 10) =>
        new() { Width = size, Height = size, Fill = brush, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>«N доменов» с правильным окончанием.</summary>
    public static string Plural(int count, string one, string few, string many)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return $"{count} {many}";

        return (count % 10) switch
        {
            1 => $"{count} {one}",
            2 or 3 or 4 => $"{count} {few}",
            _ => $"{count} {many}",
        };
    }
}
