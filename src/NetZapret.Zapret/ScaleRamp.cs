namespace NetZapret.Zapret;

/// <summary>
/// Шаг пересчёта окна (<c>nz_first2:ramp</c>) — последним в каждую секцию TCP, когда пресет
/// занижает множитель окна в SYN (<c>nz_first2:syn:…:scale=1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Множитель окна задаётся в SYN, когда имени соединения ещё нет, и SYN берёт секция
/// по адресам узлов видео. Дальше, на приветствии, winws2 отдаёт соединение секции по
/// его имени — и это не только googlevideo и youtube: те же узлы отдают обновления Chrome
/// (<c>gvt1.com</c>) и другое, а имя может забрать щит «напрямую», свой рецепт из
/// «Маршрутов», «Google (сервисы)». Windows о занижении не знает и шлёт окно в своём
/// масштабе; сервер читает его в нашем — в 128 раз меньше. Соединение, чья секция окно
/// не пересчитывает, почти встаёт: замер 11.11 — 45 КБ за 30 с у <c>dl.google.com</c>
/// через щит. Поэтому пресет пересчёт не пишет руками в каждую секцию (свои профили
/// и щит строит код), а его ставит сюда код.
/// </para>
/// <para>
/// Последним, под <c>--out-range=a</c> и <c>--payload=all</c>: шаг должен видеть каждый
/// исходящий пакет соединения до конца, пустые подтверждения тоже, а стоя последним,
/// не сдвигает диапазоны и отбор рецепта секции. Без <c>--payload=all</c> пустые пакеты
/// до lua не доходят: живой замер 14.11 — соединение с заниженным множителем шло на
/// 0,3 Мбит/с, с ним — 11,5–21,5 (64 КБ без множителя в тот же час — 5,1–9,2). Соединению
/// без заниженного множителя шаг не делает ничего и выключается на первом пакете.
/// </para>
/// </remarks>
public static class ScaleRamp
{
    /// <summary>Шаг пересчёта.</summary>
    public const string Step = "--lua-desync=nz_first2:ramp";

    /// <summary>Что встаёт в конец секции: диапазон, отбор и сам шаг.</summary>
    public static IReadOnlyList<string> Tail { get; } = ["--out-range=a", "--payload=all", Step];

    private const string Syn = "--lua-desync=nz_first2:syn";

    /// <summary>Занижает ли пресет множитель окна: шаг SYN с ключом <c>scale</c>.</summary>
    public static bool Needed(IReadOnlyList<string> arguments) => arguments.Any(a =>
        a.StartsWith(Syn, StringComparison.Ordinal)
        && a.Split(':').Skip(2).Any(part => part.StartsWith("scale=", StringComparison.Ordinal)));

    /// <summary>Дописывает шаг в конец каждой секции TCP, кроме секции самого SYN.</summary>
    public static void Apply(List<string> arguments)
    {
        if (!Needed(arguments))
            return;

        // С конца, чтобы вставка не сдвигала ещё не пройденные границы секций.
        int end = arguments.Count;

        for (int i = arguments.Count - 1; i >= -1; i--)
        {
            if (i >= 0 && arguments[i] != "--new")
                continue;

            int start = i + 1;
            var section = arguments.GetRange(start, end - start);

            if (section.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal))
                && !section.Any(a => a.StartsWith(Syn, StringComparison.Ordinal))
                && !section.Contains(Step))
            {
                arguments.InsertRange(end, Tail);
            }

            end = i;
        }
    }
}
