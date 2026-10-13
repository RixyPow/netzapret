using System.Windows;
using System.Windows.Controls;

namespace NetZapret.Gui;

/// <summary>
/// Карточки в несколько колонок: каждая следующая — в ту, что сейчас короче.
/// </summary>
/// <remarks>
/// <para>
/// «Маршруты» по макету владельца 13.10: папки карточками в две колонки.
/// Папки разной высоты — свёрнутая в одну строку, раскрытая в семь, — и
/// сетка строками (UniformGrid) оставляла бы под короткой карточкой дыру
/// высотой с соседнюю.
/// </para>
/// <para>
/// Колонок столько, сколько влезает по <see cref="ColumnWidth"/>, но не
/// больше <see cref="MaxColumns"/>: на узком окне — одна, как прежде.
/// Порядок чтения — по строкам слева направо: первая карточка в левой
/// колонке, вторая в правой, дальше — куда короче.
/// </para>
/// <para>
/// Без виртуализации: колонки не узнать, не измерив все карточки выше.
/// Поэтому строки внутри карточек лёгкие — выбор пути кнопкой с меню,
/// а не списком выбора (RoutesView, 13.10).
/// </para>
/// </remarks>
public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(
        nameof(ColumnWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(520.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Наименьшая ширина колонки.</summary>
    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Промежуток между колонками и между карточками.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private readonly List<Rect> _places = [];

    /// <summary>Ширина, на которой считались места.</summary>
    private double _measuredWidth = double.NaN;

    private int Columns(double width) =>
        double.IsInfinity(width)
            ? 1
            : Math.Clamp((int)((width + Gap) / (ColumnWidth + Gap)), 1, Math.Max(1, MaxColumns));

    private double ColumnSize(double available, int columns) =>
        double.IsInfinity(available) ? ColumnWidth : (available - Gap * (columns - 1)) / columns;

    protected override Size MeasureOverride(Size available)
    {
        int columns = Columns(available.Width);
        double width = ColumnSize(available.Width, columns);
        var heights = new double[columns];

        _places.Clear();
        _measuredWidth = available.Width;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));

            if (child.DesiredSize.Height <= 0)
            {
                _places.Add(Rect.Empty);
                continue;
            }

            int column = Shortest(heights);
            _places.Add(new Rect(column * (width + Gap), heights[column], width, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
        }

        double height = heights.Max();

        return new Size(
            double.IsInfinity(available.Width) ? width * columns + Gap * (columns - 1) : available.Width,
            Math.Max(0, height - Gap));
    }

    protected override Size ArrangeOverride(Size final)
    {
        // Ширина при раскладке бывает другой, чем при замере, — считаем места заново.
        if (_places.Count != InternalChildren.Count || Math.Abs(final.Width - _measuredWidth) > 0.5)
            MeasureOverride(final);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var place = i < _places.Count ? _places[i] : Rect.Empty;
            InternalChildren[i].Arrange(place.IsEmpty ? new Rect(0, 0, 0, 0) : place);
        }

        return final;
    }

    private static int Shortest(double[] heights)
    {
        int best = 0;

        for (int i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[best] - 0.5)
                best = i;
        }

        return best;
    }
}
