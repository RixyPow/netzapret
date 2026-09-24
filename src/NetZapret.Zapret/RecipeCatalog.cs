namespace NetZapret.Zapret;

/// <summary>Рецепт из каталога: набор шагов с объяснением, зачем он.</summary>
public sealed record CatalogRecipe
{
    /// <summary>
    /// Устойчивое имя — то, что ляжет в правило.
    /// </summary>
    /// <remarks>
    /// Своё, а не имя секции пресета. Рецепты, взятые из пресета, назывались
    /// по первой секции их группы, и стоило автору переставить секции
    /// местами, как сохранённый вчера выбор переставал находиться — молча,
    /// оборачиваясь пустым набором шагов. Здесь имя не зависит ни от чего
    /// внешнего.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>Как называется в окне.</summary>
    public required string Title { get; init; }

    /// <summary>Что делает — словами, а не перечнем ключей.</summary>
    public required string What { get; init; }

    /// <summary>Значения <c>--lua-desync</c> по порядку.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Чем примечателен: когда брать, чего опасаться.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// Рецепт подделывает пакеты.
    /// </summary>
    /// <remarks>
    /// Существенно рядом с туннелем: замер 23.08 показал, что под TUN
    /// ломаются именно рецепты с подделкой, а чистые разрезы его переживают.
    /// См. docs/coexistence.md.
    /// </remarks>
    public bool Fakes => Functions.Any(f =>
        f.Contains("fake", StringComparison.OrdinalIgnoreCase)
        || f.Contains("white", StringComparison.OrdinalIgnoreCase));

    /// <summary>Приёмы, которые рецепт вызывает.</summary>
    public IReadOnlyList<string> Functions =>
        Steps.Select(step => Before(step, ':')).ToList();

    /// <summary>Блобы, на которые рецепт ссылается.</summary>
    public IReadOnlyList<string> Blobs => Steps
        .SelectMany(step => step.Split(':'))
        .Where(part => part.StartsWith("blob=", StringComparison.Ordinal)
            || part.StartsWith("seqovl_pattern=", StringComparison.Ordinal))
        .Select(part => part[(part.IndexOf('=') + 1)..])
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Набор целиком — для тех, кто читает его глазами.
    /// </summary>
    /// <remarks>
    /// <para>
    /// С невидимыми местами переноса после двоеточий и запятых. Шаг вроде
    /// <c>hostfakesplit:host=ozon.ru:tcp_ts=-1000:repeats=4</c> не содержит
    /// ни одного пробела, и показ, не найдя где разорвать строку, рвёт её
    /// посреди слова: в узком окне набор вставал столбцом в пять букв.
    /// </para>
    /// <para>
    /// U+200B ничего не рисует и при копировании в пресет безвреден —
    /// winws2 отбрасывает его как пустое место. Пробел на его месте
    /// разделил бы аргумент надвое и сломал бы рецепт молча.
    /// </para>
    /// </remarks>
    public string Detail => string.Join("   ·   ", Steps.Select(Breakable));

    /// <summary>Расставляет места переноса внутри шага.</summary>
    private static string Breakable(string step) =>
        step.Replace(":", ":​").Replace(",", ",​");

    private static string Before(string text, char sign)
    {
        int at = text.IndexOf(sign);

        return at < 0 ? text : text[..at];
    }
}

/// <summary>Потянет ли рецепт текущий пресет.</summary>
public sealed record RecipeAvailability
{
    public required bool Runnable { get; init; }

    /// <summary>Приёмы, которых пресет не подключил.</summary>
    public required IReadOnlyList<string> MissingFunctions { get; init; }

    /// <summary>Блобы, которых пресет не объявил.</summary>
    public required IReadOnlyList<string> MissingBlobs { get; init; }

    /// <summary>Модули, которые надо дописать в пресет, чтобы рецепт заработал.</summary>
    public required IReadOnlyList<string> MissingModules { get; init; }

    /// <summary>Чего не хватает — одной строкой; у годного рецепта пусто.</summary>
    public string Complaint
    {
        get
        {
            if (Runnable)
                return string.Empty;

            var parts = new List<string>();

            if (MissingModules.Count > 0)
                parts.Add("пресет не подключает " + string.Join(", ", MissingModules));
            else if (MissingFunctions.Count > 0)
                parts.Add("движок не знает приём " + string.Join(", ", MissingFunctions));

            if (MissingBlobs.Count > 0)
                parts.Add("пресет не объявил образец " + string.Join(", ", MissingBlobs));

            return string.Join("; ", parts);
        }
    }
}

/// <summary>
/// Перечень рецептов десинка, не привязанный к пресету.
/// </summary>
/// <remarks>
/// <para>
/// До этого рецепты собирались из самого пресета — из тех наборов, что
/// в нём уже стояли. У такого источника два изъяна, и оба вылезли в работе.
/// Во-первых, выбирать можно было только из применённого: приём, который
/// движок умеет, но пресет не использует, человеку не предлагался вовсе.
/// Во-вторых, имя рецепта было именем секции, и перестановка секций
/// в пресете обесценивала сохранённый выбор.
/// </para>
/// <para>
/// Здесь наоборот: перечень свой, имена свои, а пресет отвечает лишь на
/// вопрос «потянешь ли». Наборы не выдуманы — взяты из Universal V8, где
/// выверены на живых сетях, либо из примеров в самих модулях Lua.
/// </para>
/// <para>
/// Только TCP. Выбранный рецепт проверяется рукопожатием TLS, а в UDP ему
/// проверять нечего: приёмы по UDP получали бы «не помогает» независимо
/// от собственных достоинств и сбивали бы выбор.
/// </para>
/// </remarks>
public static class RecipeCatalog
{
    /// <summary>Модули, без которых не работает почти ничто.</summary>
    /// <remarks>
    /// zapret-lib.lua даёт основу, zapret-antidpi.lua — ходовые приёмы.
    /// Пресет без них не пресет, но проверка всё равно идёт по общему
    /// правилу: предполагать здесь нечего, когда можно посмотреть.
    /// </remarks>
    public const string Base = "zapret-antidpi.lua";

    /// <summary>Имя рецепта «не трогать» — он же приём движка.</summary>
    public const string Pass = "pass";

    /// <summary>Рецепт «не трогать»: профиль с ним собирается как щит.</summary>
    public static bool IsPass(IReadOnlyList<string> steps) =>
        steps is [var only] && only.Equals(Pass, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<CatalogRecipe> All { get; } =
    [
        // Первым: это отправная точка любого подбора — работает ли имя вовсе
        // без десинка. Просьба владельца 24.09: щит для одного имени, не уводя
        // его с «десинка». В отличие от «напрямую», выбор рецепта проверяется
        // рукопожатием и сравнивается с остальными в том же окне.
        new CatalogRecipe
        {
            Id = Pass,
            Title = "Не трогать (pass)",
            What = "Пакеты с этим именем проходят мимо всего десинка: ни секции "
                + "пресета, ни свои рецепты их не трогают.",
            Steps = [Pass],
            Note = "То же, что щит у «напрямую», но для одного имени. Трафик "
                + "без имени — игровой UDP по голым адресам — щит не узнаёт, "
                + "его по-прежнему ведут секции пресета по адресам.",
        },
        new CatalogRecipe
        {
            Id = "multidisorder",
            Title = "Разрез по имени",
            What = "Режет приветствие TLS на куски по границам имени сервера "
                + "и шлёт их не по порядку.",
            Steps = ["multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1"],
            Note = "Ничего не подделывает, поэтому единственный, кто уверенно "
                + "уживается с туннелем. С него и стоит начинать.",
        },
        new CatalogRecipe
        {
            Id = "fake-multidisorder",
            Title = "Подделка и разрез по имени",
            What = "Сначала шлёт поддельное приветствие с чужим именем — его "
                + "видит фильтр, — затем настоящее вразнобой.",
            Steps =
            [
                "fake:blob=tls_google:ip_autottl=-1,3-20:ip6_autottl=-1,3-20:repeats=6:tcp_ack=-66000",
                "multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1",
            ],
            Note = "Рабочая лошадь Universal V8. Под туннелем подделки ломаются — "
                + "см. «Разрез по имени».",
        },
        new CatalogRecipe
        {
            Id = "hostfakesplit",
            Title = "Разрез с одним белым именем",
            What = "Разрезает приветствие и подмешивает к нему имя сайта, "
                + "который заведомо не блокируют.",
            Steps = ["hostfakesplit:host=ozon.ru:tcp_ts=-1000:tcp_md5:repeats=4"],
            Note = "Имя подставное и сменяемое: годится любое, до которого "
                + "провайдер пускает без помех.",
        },
        new CatalogRecipe
        {
            Id = "hostfakesplit-multi",
            Title = "Разрез с несколькими белыми именами",
            What = "То же, но белых имён несколько — фильтру труднее понять, "
                + "какое из них настоящее.",
            Steps = ["hostfakesplit_multi:hosts=google.com,vimeo.com:tcp_ts=-1000:tcp_md5:repeats=2"],
        },
        new CatalogRecipe
        {
            Id = "hostfakesplit-stealth",
            Title = "Тихий разрез с белым именем",
            What = "Та же подмена имени, но поддельный пакет один вместо двух "
                + "и повторов меньше.",
            Steps = ["hostfakesplit_stealth:host=ozon.ru:mode=soft:tcp_ts=-1000:tcp_md5"],
            Note = "Для сетей, где шумные рецепты сами навлекают внимание. "
                + "Кроме soft модуль знает blend, minimal и random — их "
                + "правят прямо в пресете.",
        },
        new CatalogRecipe
        {
            Id = "tls-multisplit-sni",
            Title = "Разрез с наложением по имени",
            What = "Режет по имени сервера и накладывает куски друг на друга "
                + "так, что фильтр собирает не то, что соберёт сервер.",
            Steps = ["tls_multisplit_sni:seqovl=652:seqovl_pattern=tls_google"],
        },
        new CatalogRecipe
        {
            Id = "syndata",
            Title = "Данные в первом пакете",
            What = "Кладёт поддельное приветствие прямо в пакет установки "
                + "соединения, до того как фильтр начнёт следить.",
            Steps = ["syndata:blob=tls_google"],
            Note = "Помогает там, где фильтр включается со второго пакета. "
                + "Разрезов не делает — часто берётся в пару к ним.",
        },
        new CatalogRecipe
        {
            Id = "flood-white",
            Title = "Залп белыми приветствиями",
            What = "Перед настоящим приветствием шлёт десяток поддельных "
                + "с незаблокированным именем — фильтр считает соединение белым.",
            Steps = ["flood_white:blob=tls_max:count=10:badsum:pos=1,midsld"],
            Note = "Против блокировки «16 КБ» на Cloudflare и Ростелекоме. "
                + "Шумный: два десятка лишних пакетов на каждое соединение.",
        },
        new CatalogRecipe
        {
            Id = "ttl-ladder",
            Title = "Лесенка по дальности",
            What = "Шлёт поддельные приветствия с растущей дальностью жизни "
                + "пакета: какое-то дойдёт до фильтра, но не до сервера.",
            Steps = ["ttl_ladder:blob=tls_max:ttl_min=4:ttl_max=10:pos=1,midsld"],
            Note = "Когда неизвестно, на каком переходе стоит фильтр.",
        },
        new CatalogRecipe
        {
            Id = "white-sandwich",
            Title = "Белое с двух сторон",
            What = "Окружает каждый кусок настоящего приветствия поддельными — "
                + "до и после.",
            Steps = ["white_sandwich:blob=tls_max:before=5:after=2:badsum:pos=1,midsld"],
        },
        new CatalogRecipe
        {
            Id = "seqovl-white",
            Title = "Наложение белым именем",
            What = "Первая часть пакета уходит дважды, и в накладывающейся "
                + "копии стоит незаблокированное имя.",
            Steps = ["seqovl_white:blob=tls_max:ovl_size=150:pos=1,midsld"],
        },
    ];

    /// <summary>Рецепт по имени; <c>null</c> — такого в каталоге нет.</summary>
    public static CatalogRecipe? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(r =>
                  r.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Потянет ли рецепт этот пресет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Приём должен быть объявлен в подключённом модуле, а блоб — в шапке
    /// пресета. Не хватило любого — winws2 не поднимется вовсе: он отвечает
    /// «desync function does not exist» и выходит, оставляя человека без
    /// десинка целиком, а не без одного правила.
    /// </para>
    /// <para>
    /// Недостающий модуль называется отдельно от недостающего приёма.
    /// «Пресет не подключает zapret-16kb.lua» — это указание, что дописать;
    /// «движок не знает приём flood_white» означает, что модуля нет и на
    /// диске, и дописывать нечего.
    /// </para>
    /// </remarks>
    public static RecipeAvailability Check(
        CatalogRecipe recipe,
        ZapretPreset preset,
        IReadOnlyDictionary<string, string> providers)
    {
        var connected = LuaModules.DeclaredBy(preset);
        var blobs = LuaModules.BlobsOf(preset);

        var unknown = new List<string>();
        var modules = new List<string>();

        foreach (var name in recipe.Functions)
        {
            if (!providers.TryGetValue(name, out var module))
            {
                unknown.Add(name);
            }
            else if (!connected.Contains(module) && !modules.Contains(module))
            {
                modules.Add(module);
            }
        }

        var absent = recipe.Blobs.Where(b => !blobs.Contains(b)).ToList();

        return new RecipeAvailability
        {
            Runnable = unknown.Count == 0 && modules.Count == 0 && absent.Count == 0,
            MissingFunctions = unknown,
            MissingBlobs = absent,
            MissingModules = modules,
        };
    }
}
