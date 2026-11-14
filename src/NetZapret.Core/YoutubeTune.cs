namespace NetZapret.Core;

/// <summary>
/// Настройка десинка для узлов видео YouTube — итог автонастройки на вкладке «Десинк»
/// (<c>YoutubeTuner</c>), поверх любого пресета Universal (<c>YoutubeAddon</c>).
/// </summary>
/// <remarks>
/// До 14.11 рецепт узлов видео жил только в отдельном пресете «Universal V13 YT FIX»:
/// перейти на него значило потерять свой пресет. А нужен он не всем, и не одинаково:
/// узлы, закрытые наглухо, далёкие узлы, мёртвый QUIC — у каждой сети своё (обсуждение
/// №63; отчёты из Тулы и СПб 11.11). Поэтому решает замер, а настройка — надстройка
/// к тому пресету, что выбран.
/// </remarks>
public sealed record YoutubeTune
{
    /// <summary>Рецепт узлов видео: малое окно по надобности и сброс зависшего соединения.</summary>
    public bool Nodes { get; init; }

    /// <summary>Малое окно вдвое шире (множитель 1, до 131 КБ) — ценой перехвата подтверждений.</summary>
    public bool Doubled { get; init; }

    /// <summary>Задержка в мс, выше которой группе узлов прощается первый срез.</summary>
    public int FarMs { get; init; } = 35;

    /// <summary>QUIC к узлам видео выбрасывать: плеер сразу идёт по TCP, не ждёт.</summary>
    public bool DropQuic { get; init; }

    /// <summary>Когда мерили.</summary>
    public DateTime? Measured { get; init; }

    /// <summary>Итог замера одной строкой — для карточки «Десинка».</summary>
    public string? Summary { get; init; }

    /// <summary>Меняет ли настройка что-нибудь в строке winws2.</summary>
    public bool Active => Nodes || DropQuic;
}
