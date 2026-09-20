using System.Windows.Media;
using System.Windows.Shapes;

namespace NetZapret.Gui;

/// <summary>
/// Поворот стрелки сворачивания.
/// </summary>
/// <remarks>
/// <para>
/// Стрелка рисуется фигурой (стиль <c>Chevron</c> в Theme/Controls.xaml),
/// а не знаком шрифта, и состояние у неё — угол поворота, а не текст.
/// Свёрнутая смотрит вправо, развёрнутая — вниз.
/// </para>
/// <para>
/// Помощник заведён ради единственного места, где живёт это «вправо-вниз».
/// Раскрывающихся карточек в окне пять, и когда каждая решала сама, они
/// разошлись: подписки рисовали «►/▼», маршруты и хосты «▸/▾». Разница
/// была не только в рисунке. Первой пары в Segoe UI нет вовсе — ни U+25B8,
/// ни U+25BE, — и Windows подменяла их чужим шрифтом, тогда как «►» U+25BA
/// и «▼» U+25BC в нём свои. Один жест выходил двумя гарнитурами, и владелец
/// увидел это как «стрелочки кривые».
/// </para>
/// </remarks>
internal static class Chevrons
{
    /// <summary>Угол для развёрнутого состояния.</summary>
    public const double Open = 90;

    /// <summary>Угол для свёрнутого.</summary>
    public const double Shut = 0;

    /// <summary>Какой угол положен состоянию.</summary>
    public static double Angle(bool open) => open ? Open : Shut;

    /// <summary>
    /// Повернуть стрелку под состояние.
    /// </summary>
    /// <remarks>
    /// Заводит свой <see cref="RotateTransform"/>, а не правит тот, что
    /// пришёл из стиля: стиль один на все стрелки окна, и его
    /// преобразование заморожено — правка на месте уронила бы приложение.
    /// </remarks>
    public static void Turn(Path arrow, bool open)
    {
        arrow.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        arrow.RenderTransform = new RotateTransform(Angle(open));
    }
}
