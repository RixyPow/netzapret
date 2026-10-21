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

    /// <summary>
    /// Что тема сама сказала про «Главную» (<c>homeFullWidth</c>): на всю
    /// ширину или с картинкой в карточке справа; <c>null</c> — не сказала.
    /// </summary>
    /// <remarks>
    /// Владелец 08.10 про «Рыбку»: «по умолчанию растягивать главную на всё
    /// окно». Не всякий фон годится в карточку справа: ролик «Рыбки» —
    /// 132×74 точки, растянутый на карточку, — размытое пятно.
    /// </remarks>
    public bool? HomeFullWidth { get; init; }

    /// <summary>
    /// «Главная» с этой темой на всю ширину — если человек не выбрал иначе
    /// (<see cref="AppSettings.HomeWide"/>).
    /// </summary>
    /// <remarks>
    /// Не сказала тема — решает картинка (владелец 20.10: «пусть все темы
    /// без фона растягивают главную по умолчанию на всю строку»): без неё
    /// карточке справа нечего показать, и колонка стояла бы пустой.
    /// </remarks>
    public bool HomeWide => HomeFullWidth ?? Background is null;

    public ThemeColor this[string slot] => Colors[slot];

    /// <summary>
    /// Цвет значка: заданный темой (<see cref="ThemeSlots.Logo"/>) или её же —
    /// верх цвета главной кнопки, кривая — фон окна, низ — текст.
    /// </summary>
    /// <remarks>
    /// Так значок любой темы, и не знающей про него, — в её цветах. Верх —
    /// заливкой кнопки «Запустить» (владелец 21.10: «самое лаконичное, это
    /// когда цвет верхней части такой же, как и у главной кнопки»; до того
    /// утром — «Работает»). Кривая фоном окна читается прорезью в плитке,
    /// как серая на листе вариантов владельца, а текст на фоне окна заведомо
    /// виден — его сверяет проверка читаемости.
    /// </remarks>
    public ThemeColor Logo(string slot) =>
        Colors.TryGetValue(slot, out var set)
            ? set
            : slot switch
            {
                ThemeSlots.LogoTop => Colors[ThemeSlots.AccentFill],
                ThemeSlots.LogoBand => Colors[ThemeSlots.Backdrop],
                ThemeSlots.LogoBottom => Colors[ThemeSlots.Text],
                _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "не цвет значка"),
            };
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
    /// <summary>Картинка или видео (mp4), полный путь внутри папки темы.</summary>
    public required string Image { get; init; }

    /// <summary>
    /// Фон — видео: играет по кругу, стекла под карточками нет.
    /// </summary>
    /// <remarks>
    /// Стекло считается из фона один раз при применении темы; у живого
    /// фона оно отставало бы от картинки. Владелец 07.10: «стекло не надо,
    /// пусть только прозрачность будет» — карточки полупрозрачные прямо
    /// поверх видео.
    /// </remarks>
    public bool IsVideo => Path.GetExtension(Image).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Где на «Главной» середина картинки, долей ширины страницы; <c>null</c> —
    /// как прежде, по середине колонки арта (<see cref="SideArtLayout"/>).
    /// </summary>
    /// <remarks>
    /// Владелец 15.10: «сделай миниатюру главной и дай возможность разместить
    /// там фон, всё остальное заливается градиентом». Картинка кладётся
    /// в редакторе тем куда угодно и любого размера; что она не закрывает —
    /// градиент из фона окна в цвет картинки.
    /// </remarks>
    public double? HomeX { get; init; }

    /// <summary>Где середина картинки по высоте, долей высоты страницы.</summary>
    public double? HomeY { get; init; }

    /// <summary>Высота картинки долей высоты страницы.</summary>
    public double? HomeSize { get; init; }

    /// <summary>Картинка на «Главной» уложена руками.</summary>
    public bool HomePlaced => HomeX is not null && HomeY is not null && HomeSize is not null;
}
