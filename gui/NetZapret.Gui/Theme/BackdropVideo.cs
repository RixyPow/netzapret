using System.Windows;
using System.Windows.Media;
using NetZapret.Core.Themes;

namespace NetZapret.Gui;

/// <summary>
/// Живой фон темы — видео mp4 по кругу (владелец 07.10: «именно как фон
/// эта гифка должна быть»).
/// </summary>
/// <remarks>
/// <para>
/// Видео рисуется кистью: <see cref="VideoDrawing"/> с проигрывателем
/// в <see cref="DrawingBrush"/>. Ею заливается тот же прямоугольник фона,
/// что и картинкой, и колонка арта на «Главной» — разметка окна о видео
/// не знает.
/// </para>
/// <para>
/// Проигрыватель один на окно. Тема может собраться и тут же не пройти
/// проверку — поэтому собранная тема его только открывает, а играть он
/// начинает, когда тема легла (<see cref="Take"/>). Окно скрыто в трей,
/// свёрнуто или анимации выключены — пауза: движущийся фон заставляет
/// окно перерисовываться без остановки (так 30.09 «Замер скорости» держал
/// поток окна занятым на простое).
/// </para>
/// </remarks>
public static class BackdropVideo
{
    /// <summary>Ключ словаря темы, под которым лежит рисунок видео.</summary>
    public const string Key = "BackdropVideo";

    private static MediaPlayer? _player;

    /// <summary>Рисунок видео для кистей; проигрыватель открыт, но не играет.</summary>
    internal static VideoDrawing Create(string path)
    {
        var player = new MediaPlayer { IsMuted = true, Volume = 0 };

        // Пропорции — сразу, из заголовка файла: кисти и колонка арта
        // раскладываются при применении темы, а проигрыватель узнаёт размер
        // кадра позже, когда файл открылся.
        var (width, height) = VideoFile.FrameSize(path) ?? (16, 9);
        var drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, width, height) };

        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
                drawing.Rect = new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight);

            if (ReferenceEquals(player, _player))
                Update();
        };

        // По кругу: в конце — на начало.
        player.MediaEnded += (_, _) =>
        {
            player.Position = TimeSpan.Zero;

            if (ReferenceEquals(player, _player) && ShouldPlay)
                player.Play();
        };

        player.Open(new Uri(path));

        return drawing;
    }

    /// <summary>Тема легла: её видео играет, прежнее закрывается.</summary>
    internal static void Take(ResourceDictionary dictionary)
    {
        var next = (dictionary.Contains(Key) ? dictionary[Key] as VideoDrawing : null)?.Player;

        if (_player is { } old && !ReferenceEquals(old, next))
            old.Close();

        _player = next;
        Update();
    }

    /// <summary>Тема не легла: её проигрыватель закрывается, текущий не трогается.</summary>
    internal static void Discard(ResourceDictionary dictionary)
    {
        if (dictionary.Contains(Key) && dictionary[Key] is VideoDrawing { Player: { } player } && !ReferenceEquals(player, _player))
            player.Close();
    }

    /// <summary>
    /// Играть или стоять — по окну и выключателю анимаций; звать при их смене.
    /// </summary>
    public static void Update()
    {
        if (_player is not { } player)
            return;

        if (ShouldPlay)
        {
            player.Play();
            return;
        }

        // Стоящий фон — первым кадром, а не чернотой: пауза до первого
        // показа кадра не рисует ничего.
        player.Play();
        player.Pause();
    }

    private static bool ShouldPlay =>
        Motion.Enabled
        && Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };
}
