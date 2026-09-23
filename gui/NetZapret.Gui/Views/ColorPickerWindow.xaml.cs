using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetZapret.Core.Themes;

namespace NetZapret.Gui.Views;

/// <summary>
/// Выбор цвета — как в Telegram: квадрат, полоса оттенка, HSL, RGB и HEX.
/// </summary>
/// <remarks>
/// Состояние держится в HSV: квадрат и полоса — это оно само. Держи мы его
/// в RGB, у чёрного и серых терялся бы оттенок, и полоса прыгала бы в
/// красный при каждом касании квадрата у нижнего края.
/// </remarks>
public partial class ColorPickerWindow : Window
{
    private readonly ThemeColor _old;
    private double _h, _s, _v;
    private bool _drag;

    public ThemeColor Chosen { get; private set; }

    public ColorPickerWindow(ThemeColor start, string title)
    {
        InitializeComponent();

        _old = start;
        Chosen = start;
        Heading.Text = title;
        Title = title;

        (_h, _s, _v) = ColorMath.ToHsv(start);
        OldSwatch.Background = new SolidColorBrush(ToColor(start));

        Loaded += (_, _) => Show(updateFields: true);
    }

    private ThemeColor Current => ColorMath.FromHsv(_h, _s, _v, _old.A);

    private void Show(bool updateFields)
    {
        var color = Current;
        Chosen = color;

        HueStop.Color = ToColor(ColorMath.FromHsv(_h, 1, 1));
        NewSwatch.Background = new SolidColorBrush(ToColor(color));

        Canvas.SetLeft(SquareMark, _s * Square.ActualWidth - SquareMark.Width / 2);
        Canvas.SetTop(SquareMark, (1 - _v) * Square.ActualHeight - SquareMark.Height / 2);

        // Кольцо тёмное на светлом и светлое на тёмном — иначе теряется.
        SquareMark.Stroke = _v > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;

        Canvas.SetLeft(HueMark, _h / 360 * HueBar.ActualWidth - HueMark.Width / 2);

        if (!updateFields)
            return;

        var (h, s, l) = ColorMath.ToHsl(color);

        HField.Text = Math.Round(h).ToString(CultureInfo.InvariantCulture);
        SField.Text = Math.Round(s * 100).ToString(CultureInfo.InvariantCulture);
        LField.Text = Math.Round(l * 100).ToString(CultureInfo.InvariantCulture);
        RField.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GField.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BField.Text = color.B.ToString(CultureInfo.InvariantCulture);
        HexField.Text = $"{color.R:x2}{color.G:x2}{color.B:x2}";
    }

    private void OnSquareDown(object sender, MouseButtonEventArgs e)
    {
        _drag = true;
        Square.CaptureMouse();
        PickSquare(e.GetPosition(Square));
    }

    private void OnSquareMove(object sender, MouseEventArgs e)
    {
        if (_drag)
            PickSquare(e.GetPosition(Square));
    }

    private void OnSquareUp(object sender, MouseButtonEventArgs e)
    {
        _drag = false;
        Square.ReleaseMouseCapture();
    }

    private void PickSquare(Point at)
    {
        _s = Math.Clamp(at.X / Square.ActualWidth, 0, 1);
        _v = 1 - Math.Clamp(at.Y / Square.ActualHeight, 0, 1);
        Show(updateFields: true);
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _drag = true;
        HueBar.CaptureMouse();
        PickHue(e.GetPosition(HueBar));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_drag)
            PickHue(e.GetPosition(HueBar));
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        _drag = false;
        HueBar.ReleaseMouseCapture();
    }

    private void PickHue(Point at)
    {
        _h = Math.Clamp(at.X / HueBar.ActualWidth, 0, 1) * 359.9;
        Show(updateFields: true);
    }

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnField(sender, e);
    }

    /// <summary>Поле правили — пересчитать цвет из него; неразборчивое — вернуть прежнее.</summary>
    private void OnField(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } box)
            return;

        var text = box.Text.Trim().TrimStart('#');
        var color = Current;

        static bool Number(string s, out double v) =>
            double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        ThemeColor? next = null;

        switch (key)
        {
            case "hex" when ThemeColor.TryParse("#" + text, out var parsed) && text.Length == 6:
                next = parsed with { A = _old.A };
                break;

            case "r" or "g" or "b" when Number(text, out var c):
                byte v = (byte)Math.Clamp(Math.Round(c), 0, 255);
                next = key switch
                {
                    "r" => color with { R = v },
                    "g" => color with { G = v },
                    _ => color with { B = v },
                };
                break;

            case "h" or "s" or "l" when Number(text, out var n):
                var (h, s, l) = ColorMath.ToHsl(color);
                next = key switch
                {
                    "h" => ColorMath.FromHsl(n, s, l, _old.A),
                    "s" => ColorMath.FromHsl(h, n / 100, l, _old.A),
                    _ => ColorMath.FromHsl(h, s, n / 100, _old.A),
                };
                break;
        }

        if (next is { } chosen)
        {
            var (nh, ns, nv) = ColorMath.ToHsv(chosen);

            // У серых оттенка нет: сохраняем прежний, чтобы полоса не прыгала.
            if (ns > 0)
                _h = nh;

            _s = ns;
            _v = nv;
        }

        Show(updateFields: true);
    }

    private void OnRevert(object sender, MouseButtonEventArgs e)
    {
        (_h, _s, _v) = ColorMath.ToHsv(_old);
        Show(updateFields: true);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Chosen = Current;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static Color ToColor(ThemeColor c) => Color.FromArgb(255, c.R, c.G, c.B);
}
