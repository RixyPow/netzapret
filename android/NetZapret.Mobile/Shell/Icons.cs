using Avalonia.Media;

namespace NetZapret.Mobile.Shell;

/// <summary>
/// Значки нижнего меню и строк — простые контуры, нарисованные здесь же.
/// </summary>
/// <remarks>
/// Своими путями, а не шрифтом значков: Segoe Fluent Icons окна Windows
/// на телефоне нет, а чужой набор значков в поставке — ещё одна лицензия
/// в «О программе» ради пяти картинок.
/// </remarks>
internal static class Icons
{
    // Домик: крыша и стены с дверью.
    public static Geometry Home { get; } = StreamGeometry.Parse(
        "M12 3 L2 11.5 L4 13.2 L5 12.4 V21 H10 V15 H14 V21 H19 V12.4 L20 13.2 L22 11.5 Z");

    // Щит — туннель, защищённый выход.
    public static Geometry Shield { get; } = StreamGeometry.Parse(
        "M12 2 L20 5 V11 C20 16 16.6 20.2 12 22 C7.4 20.2 4 16 4 11 V5 Z M12 4.2 L6 6.4 V11 C6 14.9 8.5 18.2 12 19.8 Z");

    // Развилка: один путь расходится на два — маршруты.
    public static Geometry Routes { get; } = StreamGeometry.Parse(
        "M11 22 V13.4 L5.6 8 H8 V6 H2 V12 H4 V9.4 L9 14.4 V22 Z M13 13 L14.4 14.4 L20 8.8 V11 H22 V5 H16 V7 H18.6 Z");

    // Лупа — проверка.
    public static Geometry Check { get; } = StreamGeometry.Parse(
        "M10 2 A8 8 0 1 1 10 18 A8 8 0 1 1 10 2 Z M10 4.5 A5.5 5.5 0 1 0 10 15.5 A5.5 5.5 0 1 0 10 4.5 Z M15.5 17.2 L17.2 15.5 L22.5 20.8 L20.8 22.5 Z");

    // Три точки — «Ещё».
    public static Geometry More { get; } = StreamGeometry.Parse(
        "M5 10 A2 2 0 1 1 5 14 A2 2 0 1 1 5 10 Z M12 10 A2 2 0 1 1 12 14 A2 2 0 1 1 12 10 Z M19 10 A2 2 0 1 1 19 14 A2 2 0 1 1 19 10 Z");

    public static Geometry Chevron { get; } = StreamGeometry.Parse("M8 3 L17 12 L8 21 L6.6 19.6 L14.2 12 L6.6 4.4 Z");

    public static Geometry Back { get; } = StreamGeometry.Parse("M16 3 L7 12 L16 21 L17.4 19.6 L9.8 12 L17.4 4.4 Z");

    public static Geometry Plus { get; } = StreamGeometry.Parse("M11 4 H13 V11 H20 V13 H13 V20 H11 V13 H4 V11 H11 Z");

    public static Geometry Refresh { get; } = StreamGeometry.Parse(
        "M12 4 A8 8 0 0 1 19.4 9 H22 L18 14 L14 9 H17.2 A6 6 0 1 0 18 13.4 L19.9 14.1 A8 8 0 1 1 12 4 Z");

    public static Geometry Power { get; } = StreamGeometry.Parse(
        "M11 2 H13 V12 H11 Z M6.3 5.3 L7.7 6.7 A7 7 0 1 0 16.3 6.7 L17.7 5.3 A9 9 0 1 1 6.3 5.3 Z");

    public static Geometry Dns { get; } = StreamGeometry.Parse(
        "M3 4 H21 V10 H3 Z M5 6 V8 H7 V6 Z M3 14 H21 V20 H3 Z M5 16 V18 H7 V16 Z");

    public static Geometry Speed { get; } = StreamGeometry.Parse(
        "M12 4 A10 10 0 0 1 22 14 V16 H20 V14 A8 8 0 0 0 4 14 V16 H2 V14 A10 10 0 0 1 12 4 Z M16.5 7.8 L18 9.2 L13.4 14.6 A2 2 0 1 1 11.6 12.8 Z");

    public static Geometry Doctor { get; } = StreamGeometry.Parse(
        "M10 3 H14 V10 H21 V14 H14 V21 H10 V14 H3 V10 H10 Z");

    public static Geometry Settings { get; } = StreamGeometry.Parse(
        "M3 6 H13 V8 H3 Z M17 6 H21 V8 H17 Z M13 4 H17 V10 H13 Z M3 16 H7 V18 H3 Z M11 16 H21 V18 H11 Z M7 14 H11 V20 H7 Z");

    public static Geometry Log { get; } = StreamGeometry.Parse(
        "M5 2 H15 L20 7 V22 H5 Z M7 4 V20 H18 V8 H14 V4 Z M9 11 H16 V12.6 H9 Z M9 14.5 H16 V16.1 H9 Z");

    public static Geometry Info { get; } = StreamGeometry.Parse(
        "M12 2 A10 10 0 1 1 12 22 A10 10 0 1 1 12 2 Z M12 4 A8 8 0 1 0 12 20 A8 8 0 1 0 12 4 Z M11 10 H13 V17 H11 Z M11 7 H13 V9 H11 Z");

    public static Geometry Wand { get; } = StreamGeometry.Parse(
        "M3 19.6 L15.6 7 L17 8.4 L4.4 21 Z M17 2 L18 4.5 L20.5 5.5 L18 6.5 L17 9 L16 6.5 L13.5 5.5 L16 4.5 Z");

    public static Geometry Link { get; } = StreamGeometry.Parse(
        "M14 3 H21 V10 H19 V6.4 L11.7 13.7 L10.3 12.3 L17.6 5 H14 Z M5 5 H11 V7 H7 V17 H17 V13 H19 V19 H5 Z");
}
