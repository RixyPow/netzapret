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

    public static ThemeTile From(ThemeLoad load, bool chosen, Func<string, Brush> app)
    {
        var theme = load.Theme;

        Brush Of(string slot, string fallback) =>
            theme is null ? app(fallback) : Frozen(new SolidColorBrush(ToColor(theme[slot])));

        Brush image = Frozen(new SolidColorBrush(Colors.Transparent));
        double dim = 1.0;

        if (theme?.Background is { } background)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(background.Image);
                bitmap.DecodePixelWidth = 240;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                image = Frozen(new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill });
                dim = background.Dim;
            }
            catch (Exception)
            {
                // Картинка не читается — плитка останется цветом подложки;
                // сама тема при выборе скажет, что с фоном.
            }
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

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
