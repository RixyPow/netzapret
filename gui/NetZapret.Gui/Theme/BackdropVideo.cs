using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using NetZapret.Core.Themes;

namespace NetZapret.Gui;

/// <summary>
/// Видео фона темы: рисунок с проигрывателем и укладка.
/// </summary>
/// <remarks>
/// Обычный класс, а не Freezable — потому и лежит в словаре темы. Кисть
/// с проигрывателем туда класть нельзя: WPF, запечатывая словарь, замораживает
/// всё, что в нём лежит, а проигрыватель не замораживается. Так и вышло
/// в сборке 1 (07.10): «Не удалось сменить тему: …MediaPlayer… IsFrozen
/// должно иметь значение false», а следом — падение на замороженном рисунке.
/// </remarks>
public sealed class VideoBackdrop
{
    public required VideoDrawing Drawing { get; init; }

    public required BackgroundFit Fit { get; init; }

    /// <summary>Часть кадра без чёрных полос, в долях кадра (<see cref="Letterbox"/>).</summary>
    public Rect Content { get; init; } = new(0, 0, 1, 1);

    /// <summary>Размер того, что показывается, — кадр без полос.</summary>
    public Size Size => new(Drawing.Rect.Width * Content.Width, Drawing.Rect.Height * Content.Height);

    /// <summary>Новая кисть видео — только с частью кадра без полос.</summary>
    public DrawingBrush Brush() => new(Drawing)
    {
        Viewbox = Content,
        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
    };
}

/// <summary>
/// Живой фон темы — видео mp4 по кругу (владелец 07.10: «именно как фон
/// эта гифка должна быть»).
/// </summary>
/// <remarks>
/// <para>
/// Видео рисуется кистью: <see cref="VideoDrawing"/> с проигрывателем
/// в <see cref="DrawingBrush"/>. Кисть ставится прямо слою фона окна
/// (<see cref="Attach"/>) и колонке арта «Главной» (<see cref="Drawing"/>),
/// мимо словаря ресурсов — см. <see cref="VideoBackdrop"/>. В словаре
/// у видеотемы фон прозрачный; без видео слою возвращается ссылка на него.
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
    /// <summary>Ключ словаря темы, под которым лежит <see cref="VideoBackdrop"/>.</summary>
    public const string Key = "BackdropVideo";

    private static VideoBackdrop? _current;
    private static Shape? _layer;

    /// <summary>Видео текущей темы — для колонки арта; <c>null</c> — фон не видео.</summary>
    public static VideoBackdrop? Current => _current;

    /// <summary>Видео для словаря темы; проигрыватель открыт, но не играет.</summary>
    /// <param name="content">Часть кадра без чёрных полос, в долях кадра.</param>
    internal static VideoBackdrop Create(string path, BackgroundFit fit, Rect content)
    {
        var player = new MediaPlayer { IsMuted = true, Volume = 0 };

        // Пропорции — сразу, из заголовка файла: колонка арта раскладывается
        // при применении темы, а проигрыватель узнаёт размер кадра позже,
        // когда файл открылся.
        var (width, height) = VideoFile.FrameSize(path) ?? (16, 9);
        var drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, width, height) };

        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0 && !drawing.IsFrozen)
                drawing.Rect = new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight);

            if (ReferenceEquals(player, _current?.Drawing.Player))
                Update();
        };

        // По кругу: в конце — на начало.
        player.MediaEnded += (_, _) =>
        {
            player.Position = TimeSpan.Zero;

            if (ReferenceEquals(player, _current?.Drawing.Player) && ShouldPlay)
                player.Play();
        };

        player.Open(new Uri(path));

        return new VideoBackdrop { Drawing = drawing, Fit = fit, Content = content };
    }

    /// <summary>Слой фона окна — ему видео ставится кистью напрямую.</summary>
    public static void Attach(Shape layer)
    {
        _layer = layer;
        Show();
    }

    /// <summary>Окно закрылось — его слой больше не держим (если он ещё наш).</summary>
    public static void Detach(Shape layer)
    {
        if (ReferenceEquals(_layer, layer))
            _layer = null;
    }

    /// <summary>Тема легла: её видео играет, прежнее закрывается.</summary>
    internal static void Take(ResourceDictionary dictionary)
    {
        var next = dictionary.Contains(Key) ? dictionary[Key] as VideoBackdrop : null;

        if (_current is { } old && !ReferenceEquals(old, next))
            Close(old);

        _current = next;
        Show();
        Update();
    }

    /// <summary>Тема не легла: её проигрыватель закрывается, текущий не трогается.</summary>
    internal static void Discard(ResourceDictionary dictionary)
    {
        if (dictionary.Contains(Key) && dictionary[Key] is VideoBackdrop video && !ReferenceEquals(video, _current))
            Close(video);
    }

    /// <summary>
    /// Проигрыватель и слой принадлежат потоку, где созданы, — трогать только из него.
    /// </summary>
    /// <remarks>
    /// В программе поток окна один, а в тестах каждое окно — в своём потоке:
    /// слой главного окна одного теста оставался в статическом поле, тема
    /// следующего теста лезла к нему из своего потока, и тест падал через
    /// раз («Вызывающий поток не может получить доступ к данному объекту»,
    /// 08.10, ThemeWindowsOpen).
    /// </remarks>
    private static bool Mine(System.Windows.Threading.DispatcherObject item) => item.CheckAccess();

    private static void Close(VideoBackdrop video)
    {
        if (Mine(video.Drawing.Player))
            video.Drawing.Player.Close();
    }

    /// <summary>
    /// Играть или стоять — по окну и выключателю анимаций; звать при их смене.
    /// </summary>
    public static void Update()
    {
        if (_current?.Drawing.Player is not { } player || !Mine(player))
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

    private static void Show()
    {
        if (_layer is null || !Mine(_layer))
            return;

        if (_current is { } video && Mine(video.Drawing))
            _layer.Fill = Themes.Stretch(video.Brush(), video.Fit, video.Size);
        else
            _layer.SetResourceReference(Shape.FillProperty, "BackdropImage");
    }

    private static bool ShouldPlay =>
        Motion.Enabled
        && Application.Current is { } application
        && Mine(application)
        && application.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };
}
