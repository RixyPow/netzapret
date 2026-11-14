using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Одно решение автонастройки: что, как решено и почему — числами замера.</summary>
/// <param name="What">Что решается: «Рецепт узлов видео», «Малое окно», «Далёкие узлы», «QUIC».</param>
/// <param name="Verdict">Как решено: «нужен», «вдвое шире», «от 41 мс», «не трогать».</param>
/// <param name="Why">Почему.</param>
/// <param name="On">Решение что-то включает.</param>
public sealed record YoutubeChoice(string What, string Verdict, string Why, bool On);

/// <summary>Что автонастройка решила и почему.</summary>
public sealed record YoutubeDecision
{
    /// <summary>Настройка; <c>null</c> — судить не по чему (узлов с адресом мало).</summary>
    public YoutubeTune? Tune { get; init; }

    /// <summary>По строке на решение: что и почему — числами замера.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Те же решения по частям — для таблицы окна.</summary>
    public IReadOnlyList<YoutubeChoice> Choices { get; init; } = [];

    /// <summary>
    /// Узлы не открываются и с рецептом: десинку больше нечего предложить, остаётся VPN.
    /// </summary>
    public bool NothingLeft { get; init; }
}

/// <summary>
/// Автонастройка YouTube: по замеру узлов видео (<see cref="VideoNodes"/>) — нужен ли
/// рецепт узлов, вдвое ли шире окно, с какой задержки узел «далёкий», выбрасывать ли QUIC.
/// </summary>
/// <remarks>
/// <para>
/// Пороги — из замеров 09–14.11 (подробно — в комментариях к каждому). Цель скорости —
/// <see cref="TargetMbit"/>: столько нужно одному соединению на 1440p; малое окно, которое
/// столько не даёт, стоит прощать на первом срезе (<c>far</c>), а окно, которое не даёт
/// этого и с запасом, — расширять.
/// </para>
/// <para>
/// Чего замер не видит: скорость самого видео (ссылку на поток выдаёт только плеер)
/// и задержку кэша провайдера на редком ролике. Рукопожатия могут проходить, когда данные
/// не идут (пресет loop-uh с wssize, 14.11): поэтому после применения — перезамер,
/// а не «готово» на слово.
/// </para>
/// </remarks>
public static class YoutubeTuner
{
    /// <summary>Сколько нужно одному соединению видео, Мбит/с: 1440p — около 10–15, с запасом.</summary>
    public const double TargetMbit = 25;

    /// <summary>Окно без множителя, байт.</summary>
    private const double Window = 65535;

    /// <summary>Доля ответов QUIC у узлов под фильтром, ниже которой QUIC выбрасывается.</summary>
    /// <remarks>
    /// Обсуждение №63, 13.11: 8 из 30 — плеер ждёт QUIC и идёт по TCP; дом 14.11: 21 из 30 —
    /// видео идёт по h3 на полной скорости, трогать нельзя.
    /// </remarks>
    public const double QuicFloor = 0.5;

    /// <summary>Доля ответов TCP через обход, ниже которой и с рецептом остаётся только VPN.</summary>
    public const double NothingLeftShare = 0.5;

    /// <summary>Решает по замеру.</summary>
    /// <param name="report">Замер узлов видео.</param>
    /// <param name="recipeOn">Рецепт узлов уже работал во время замера (пресет YT FIX или надстройка).</param>
    public static YoutubeDecision Decide(VideoNodesReport report, bool recipeOn)
    {
        var measured = report.Measured;

        if (measured.Count < VideoNodes.NodeFloor)
        {
            return new YoutubeDecision
            {
                Reasons = [report.Summary ?? "узлов видео с адресом слишком мало — судить не по чему"],
            };
        }

        var reasons = new List<string>();
        var choices = new List<YoutubeChoice>();

        void Say(string what, string verdict, string why, bool on)
        {
            choices.Add(new YoutubeChoice(what, verdict, why, on));
            reasons.Add($"{what.ToLowerInvariant()} — {verdict}: {why}");
        }
        var filtered = measured.Where(n => n.Filter is NodeFilter.GoogleOnly or NodeFilter.Closed).ToList();
        var closed = measured.Where(n => n.Filter == NodeFilter.Closed).ToList();
        double tcpShare = report.Tries > 0 ? (double)report.Tcp / report.Tries : 0;
        bool noCache = report.CacheNode is { GoogleSite: true };

        // Рецепт узлов: нужен, если фильтр есть или соединения теряются. Без фильтра и без
        // потерь он только тратит процессор и прибавляет срезанным 1 с на сброс.
        bool nodes = filtered.Count > 0 || tcpShare < VideoNodes.PassShare;

        if (nodes)
        {
            Say("Рецепт узлов видео", "нужен", $"под фильтром {filtered.Count} узлов из {measured.Count}"
                + (closed.Count > 0 ? $", закрыты наглухо {closed.Count}" : string.Empty)
                + $"; TCP через обход {report.Tcp} из {report.Tries}"
                + (noCache ? "; кэша Google у провайдера нет — с этих узлов идёт всё видео" : string.Empty), true);
        }
        else
        {
            Say("Рецепт узлов видео", "не нужен", $"фильтра нет, TCP {report.Tcp} из {report.Tries}", false);
        }

        // Окно: малое окно без множителя даёт 64 КБ на задержку. Узлы, до которых оно не
        // вытягивает цель, — повод расширить его вдвое (замер 14.11: 5,1–9,2 Мбит/с против
        // 11,5–21,5 на 50 мс). Ниже — не расширять: перехват подтверждений стоит процессора.
        var pings = measured.Where(n => n.PingMs is not null).Select(n => n.PingMs!.Value).ToList();
        int slow = (int)(Window * 8 / (TargetMbit * 1e6) * 1000);
        bool doubled = nodes && pings.Any(p => p > slow);

        if (nodes)
        {
            if (doubled)
                Say("Малое окно", "вдвое шире", $"пинг до узлов {Span(pings)} мс, окно 64 КБ даёт меньше {TargetMbit:0} Мбит/с уже от {slow} мс", true);
            else
                Say("Малое окно", "обычное", $"пинг до узлов {Span(pings)} мс, 64 КБ хватает на {TargetMbit:0} Мбит/с", false);
        }

        // «Далёкий» узел — тот, где и выбранное окно цели не даёт: ему первый срез прощается,
        // повтор идёт обычным соединением. Порог — от окна, а не числом с потолка.
        int far = (int)(Window * (doubled ? 2 : 1) * 8 / (TargetMbit * 1e6) * 1000);

        if (nodes)
        {
            int distant = pings.Count(p => p > far);
            Say("Далёкие узлы", $"от {far} мс", "им первый срез прощается — повтор идёт обычным соединением"
                + (distant > 0 ? $"; таких узлов {distant} из {pings.Count}" : "; таких нет"), distant > 0);
        }

        // QUIC: плеер пробует его первым. Мёртвый у узлов под фильтром — выбросить, чтобы плеер
        // не ждал; не мерился (Windows без QUIC) — не трогать, судить не по чему.
        bool dropQuic = false;
        var quicNodes = (filtered.Count > 0 ? filtered : measured).Where(n => n.QuicTries > 0).ToList();

        if (quicNodes.Count == 0)
        {
            Say("QUIC", "не трогать", "на этой машине он не мерился", false);
        }
        else
        {
            int quic = quicNodes.Sum(n => n.Quic), quicTries = quicNodes.Sum(n => n.QuicTries);
            dropQuic = quic < quicTries * QuicFloor;
            if (dropQuic)
                Say("QUIC к узлам видео", "выбросить", $"проходит {quic} из {quicTries}, плеер ждал бы его и потом шёл по TCP", true);
            else
                Say("QUIC", "не трогать", $"проходит {quic} из {quicTries}, видео пойдёт по нему", false);
        }

        // Последний шаг — только когда рецепт уже работал и не помог: узлы под фильтром
        // и через обход отвечают меньше чем в половине случаев.
        int filteredTcp = filtered.Sum(n => n.Tcp), filteredTries = filtered.Sum(n => n.Tries);
        bool nothingLeft = recipeOn && filteredTries > 0 && filteredTcp < filteredTries * NothingLeftShare
            && (dropQuic || quicNodes.Count == 0 || quicNodes.Sum(n => n.Quic) < quicNodes.Sum(n => n.QuicTries) * QuicFloor);

        if (nothingLeft)
        {
            Say("Узлы под фильтром", "не открываются и с рецептом", $"TCP {filteredTcp} из {filteredTries} — "
                + "десинку больше нечего предложить; остаётся YouTube через VPN, и страница, и видео", false);
        }

        return new YoutubeDecision
        {
            Tune = new YoutubeTune
            {
                Nodes = nodes,
                Doubled = doubled,
                FarMs = far,
                DropQuic = dropQuic,
                Measured = DateTime.Now,
                Summary = report.Totals,
            },
            Reasons = reasons,
            Choices = choices,
            NothingLeft = nothingLeft,
        };
    }

    private static string Span(IReadOnlyList<int> pings) => pings.Count == 0
        ? "не измерен"
        : pings.Min() == pings.Max() ? $"{pings.Min()}" : $"{pings.Min()}–{pings.Max()}";
}
