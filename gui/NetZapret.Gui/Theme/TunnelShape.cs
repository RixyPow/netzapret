namespace NetZapret.Gui;

/// <summary>
/// Волна туннельного эффекта на карточке состояния «Главной» — вычисление
/// без рисования (рисует <see cref="TunnelWave"/>).
/// </summary>
/// <remarks>
/// <para>
/// Владелец 05.11 выбрал из картинок «туннельный эффект» — то же, что значок
/// программы: волновые пакеты идут слева, у барьера (DPI) часть проходит
/// и бежит дальше ниже ростом, остальное отражается назад. Пропущенная доля —
/// это и есть состояние: работает — проходит заметная часть, с оговорками —
/// меньше, движок упал — ничего.
/// </para>
/// <para>
/// Пакет — гауссов горб, двигающийся вправо. Левее барьера рисуется он сам,
/// правее — он же, умноженный на долю прохода; отражение — его зеркало
/// относительно барьера, идущее назад, со своей долей. Физики больше нет:
/// важно, чтобы глаз читал «прошло сквозь стену», а не точность.
/// </para>
/// </remarks>
public static class TunnelShape
{
    /// <summary>Пакет: середина горба по горизонтали, высота, ширина горба, скорость (точек в секунду).</summary>
    public sealed record Packet(double X, double Height, double Spread, double Speed);

    /// <summary>
    /// Высота волны над линией в точке <paramref name="x"/>: левее барьера —
    /// пакеты как есть, правее — их прошедшая доля <paramref name="pass"/>.
    /// </summary>
    public static double Wave(IReadOnlyList<Packet> packets, double x, double barrier, double pass)
    {
        double y = 0;

        foreach (var p in packets)
            y += Hump(p, x) * (x < barrier ? 1 : pass);

        return y;
    }

    /// <summary>
    /// Отражённое: зеркало пакета относительно барьера, только левее него и только
    /// с тех пор, как пакет до барьера дошёл. Доля — то, что не прошло.
    /// </summary>
    public static double Echo(IReadOnlyList<Packet> packets, double x, double barrier, double pass)
    {
        if (x >= barrier)
            return 0;

        double y = 0;

        foreach (var p in packets)
        {
            if (p.X < barrier - p.Spread * 2.5)
                continue;

            y += Hump(p with { X = 2 * barrier - p.X }, x) * (1 - pass);
        }

        return y;
    }

    /// <summary>
    /// Насколько волна видна в точке: от левого края она проявляется постепенно —
    /// там лежит надпись «Работает», — и гаснет у правого.
    /// </summary>
    /// <param name="calm">Откуда (долей ширины) волна в полную силу.</param>
    public static double Fade(double x, double width, double calm)
    {
        if (width <= 0)
            return 0;

        double rise = Smooth(x / Math.Max(1, width * calm));
        double fall = Smooth((width - x) / 60);

        return rise * fall;
    }

    /// <summary>Пакет ушёл с карточки, и его отражение тоже: его пора убрать.</summary>
    public static bool Gone(Packet p, double barrier, double width) =>
        p.X - p.Spread * 3 > width && 2 * barrier - p.X + p.Spread * 3 < 0;

    private static double Hump(Packet p, double x)
    {
        double u = (x - p.X) / p.Spread;
        return p.Height * Math.Exp(-u * u);
    }

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
