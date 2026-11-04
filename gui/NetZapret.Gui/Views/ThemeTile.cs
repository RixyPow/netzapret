using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Core.Themes;

namespace NetZapret.Gui.Views;

/// <summary>
/// Плитка темы в «Ещё» — как выбор тем в Telegram (владелец, 23.09).
/// </summary>
/// <remarks>
/// Плитка рисуется цветами самой темы, а не текущей: подложка или картинка
/// фона, «сообщение» цветом карточки, «ответ» цветом главной кнопки и
/// кружок выбора. Название — словами под плиткой: выбор видно и по кружку,
/// и по рамке, и по цвету подписи, а не только по одному из них.
/// </remarks>
public sealed record ThemeTile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool Ok { get; init; }
    public required bool Chosen { get; init; }

    /// <summary>Подсказка: автор или причина, по которой тема не применяется.</summary>
    public required string Tip { get; init; }

    public required Brush Backdrop { get; init; }
    public required Brush Surface { get; init; }
    public required Brush Fill { get; init; }
    public required Brush Ring { get; init; }
    public required Brush Dot { get; init; }

    /// <summary>Картинка фона, уменьшенная; прозрачная кисть — фона нет.</summary>
    public required Brush Image { get; init; }

    /// <summary>Затемнение картинки — как в окне, чтобы плитка не врала.</summary>
    public required double Dim { get; init; }

    public required Brush Edge { get; init; }
    public required Brush NameColor { get; init; }

    public Visibility DotShown => Chosen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Непрошедшая проверку тема видна, но приглушена: её не выбрать.</summary>
    public double Fade => Ok ? 1.0 : 0.45;

    /// <summary>
    /// Плитка темы; <paramref name="decode"/> = false — картинку фона брать
    /// только уже раскодированную (<see cref="PrepareAsync"/>), иначе плитка
    /// пока без неё.
    /// </summary>
    public static ThemeTile From(ThemeLoad load, bool chosen, Func<string, Brush> app, bool decode = true)
    {
        var theme = load.Theme;

        Brush Of(string slot, string fallback) =>
            theme is null ? app(fallback) : Frozen(new SolidColorBrush(ToColor(theme[slot])));

        Brush image = Frozen(new SolidColorBrush(Colors.Transparent));
        double dim = 1.0;

        if (theme?.Background is { } background && Key(background) is { } key
            && (decode ? Pictures.GetOrAdd(key, _ => Decode(background)) : Pictures.GetValueOrDefault(key)) is { } bitmap)
        {
            image = Frozen(new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill });
            dim = background.Dim;
        }

        var tip = load.Ok
            ? (theme?.Author is { } author ? $"{theme.Name} — {author}" : theme?.Name ?? load.Id)
            : "Не применяется: " + string.Join("; ", load.Problems.Take(3))
              + (load.Problems.Count > 3 ? $" и ещё {load.Problems.Count - 3}" : string.Empty);

        return new ThemeTile
        {
            Id = load.Id,
            Name = theme?.Name ?? load.Id,
            Ok = load.Ok,
            Chosen = chosen,
            Tip = tip,
            Backdrop = Of(ThemeSlots.Backdrop, "Backdrop"),
            Surface = Of(ThemeSlots.Surface, "Surface"),
            Fill = Of(ThemeSlots.AccentFill, "AccentFill"),
            Ring = Of(ThemeSlots.Muted, "Muted"),
            Dot = Of(ThemeSlots.Accent, "Accent"),
            Image = image,
            Dim = dim,
            Edge = app(chosen ? "Accent" : "Border"),
            NameColor = app(chosen ? "Accent" : "Muted"),
        };
    }

    /// <summary>
    /// Картинки плиток, уже уменьшенные, — по файлу: путь, размер, время записи.
    /// </summary>
    /// <remarks>
    /// Симуляция 05.11 (окно на настройках владельца, разделы по кругу, стек
    /// потока окна на подвисании): заход в «Оформление» держал поток окна
    /// 390–405 мс, и стоял он в <c>BitmapImage.EndInit</c> из <see cref="From"/>.
    /// Раздел создаётся заново при каждом заходе, и картинки тем раскодировались
    /// каждый раз заново. Готовая картинка заморожена: ей всё равно, в каком потоке
    /// её сделали и сколько плиток её покажут. Тема сменила фон — файл другой
    /// по размеру или времени, ключ другой.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, BitmapSource?> Pictures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Все ли картинки этих тем уже раскодированы.</summary>
    public static bool Ready(IEnumerable<ThemeLoad> loads) =>
        loads.All(load => load.Theme?.Background is not { } background
            || Key(background) is not { } key || Pictures.ContainsKey(key));

    /// <summary>
    /// Раскодирует недостающие картинки в своём потоке, не в потоке окна.
    /// </summary>
    /// <remarks>
    /// Поток STA, как у окна: кадр видео даёт оболочка Windows через COM
    /// (<see cref="VideoPoster"/>), а она так и вызывалась до сих пор.
    /// </remarks>
    public static Task PrepareAsync(IReadOnlyList<ThemeLoad> loads)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                foreach (var load in loads)
                {
                    if (load.Theme?.Background is { } background && Key(background) is { } key)
                        Pictures.GetOrAdd(key, _ => Decode(background));
                }

                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Картинки тем",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>Ключ картинки; <c>null</c> — файла нет, и плитка без картинки.</summary>
    private static string? Key(ThemeBackground background)
    {
        try
        {
            var file = new FileInfo(background.Image);
            return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(ThemeBackground background)
    {
        try
        {
            if (background.IsVideo)
            {
                // Живой фон — кадром, который показывает проводник: mp4
                // картинкой не раскодируется, и плитка выходила пустой
                // (владелец 07.10: «нету рыбки»).
                var frame = VideoPoster.Load(background.Image, size: 240);

                // Без чёрных полос по краям — как его покажет окно.
                return frame is null ? null : VideoPoster.Crop(frame, VideoPoster.Content(frame));
            }

            var picture = new BitmapImage();
            picture.BeginInit();
            picture.UriSource = new Uri(background.Image);
            picture.DecodePixelWidth = 240;
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.EndInit();
            picture.Freeze();
            return picture;
        }
        catch (Exception)
        {
            // Картинка не читается — плитка останется цветом подложки;
            // сама тема при выборе скажет, что с фоном.
            return null;
        }
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
