namespace NetZapret.Core.Themes;

/// <summary>
/// Тема оформления: 14 цветов, 3 шрифта и фон — всё, что художник вправе менять.
/// </summary>
/// <remarks>
/// <para>
/// Договор описан в системе дизайна на claude.ai и в docs/roadmap.md,
/// раздел «Свои темы оформления». Отступы, размеры и скругления сюда
/// не входят намеренно: попади они в тему, каждая правка разметки ломала
/// бы чужие темы, и чинить их пришлось бы нам.
/// </para>
/// <para>
/// Тема — данные, а не код. Разметка WPF умеет создавать объекты и выполнять
/// код, и чужой словарь XAML был бы чужой программой внутри нашей; поэтому
/// JSON, цвета строками и имена файлов внутри папки темы.
/// </para>
/// </remarks>
public sealed record Theme
{
    /// <summary>Имя папки — по нему тема записывается в настройки.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    /// <summary>Папка темы, полный путь: от неё отсчитываются фон и шрифты.</summary>
    public required string Folder { get; init; }

    /// <summary>Все 14 цветов, ключ — имя слота из <see cref="ThemeSlots.All"/>.</summary>
    public required IReadOnlyDictionary<string, ThemeColor> Colors { get; init; }

    public required ThemeFonts Fonts { get; init; }

    /// <summary>Фон окна; <c>null</c> — сплошной цвет <c>backdrop</c>.</summary>
    public ThemeBackground? Background { get; init; }

    public ThemeColor this[string slot] => Colors[slot];
}

/// <summary>
/// Три шрифта темы.
/// </summary>
/// <remarks>
/// Третий, <see cref="Display"/>, — решение владельца 23.09: под тему вроде
/// Nirvana нужен шрифт логотипа, а таблицу им не прочитать. Он только для
/// заголовков вкладок; данные всегда идут <see cref="Mono"/>.
/// </remarks>
public sealed record ThemeFonts
{
    public required string Ui { get; init; }

    public required string Mono { get; init; }

    public required string Display { get; init; }

    /// <summary>Файлы шрифтов из папки темы, полные пути; пусто — только системные.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>Как укладывать картинку фона.</summary>
public enum BackgroundFit
{
    /// <summary>Растянуть на всё окно с обрезкой краёв.</summary>
    Cover,

    /// <summary>Вписать целиком; поля — цвет <c>backdrop</c>.</summary>
    Contain,

    /// <summary>Замостить.</summary>
    Tile,
}

/// <summary>
/// Фон окна: картинка, затемнение и размытие под карточками.
/// </summary>
/// <remarks>
/// Решения владельца 23.09: фон и под боковым меню; сам фон чёткий,
/// размыт он только под карточками и меню — как матовое стекло.
/// </remarks>
public sealed record ThemeBackground
{
    /// <summary>Картинка, полный путь внутри папки темы.</summary>
    public required string Image { get; init; }

    public BackgroundFit Fit { get; init; } = BackgroundFit.Cover;

    /// <summary>
    /// Затемнение слоем цвета <c>backdrop</c>, от 0 до 1.
    /// </summary>
    /// <remarks>
    /// Им художник и добивается читаемости: заголовки вкладок стоят прямо
    /// на картинке, и без затемнения яркая картинка съедает текст.
    /// </remarks>
    public double Dim { get; init; } = 0.5;

    /// <summary>Радиус размытия под карточками и меню, в точках; 0 — без размытия.</summary>
    public double Blur { get; init; } = 24;
}
