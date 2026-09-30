namespace NetZapret.Proxy;

/// <summary>Что сторожу сделать с выходом по итогам проверки.</summary>
public enum ExitAction
{
    /// <summary>Ничего.</summary>
    Keep,

    /// <summary>Подключённый выход молчит — искать замену.</summary>
    Replace,

    /// <summary>Выбранный руками сервер снова отвечает — вернуть его.</summary>
    Restore,
}

/// <summary>
/// Решения сторожа о подключённом выходе: когда искать замену и когда
/// возвращать выбранный руками сервер.
/// </summary>
/// <remarks>
/// <para>
/// Сервер, выбранный человеком, сторож до 30.09 мерил, но не менял: «он выбран
/// человеком». Цена — заминки: при молчании такого сервера туннель ждал его,
/// пока не срабатывал обход (три проверки трафика, полторы минуты), а у владельца
/// 30.09 были провалы по 30–40 секунд. Владелец в тот же день: замену — настройкой.
/// </para>
/// <para>
/// Замена временная. Выбранный сервер проверяется дальше, одним соединением
/// за проверку, и возвращается, когда ответит дважды подряд: один ответ бывает
/// и у «мигающего» сервера, а качаться туда и обратно хуже, чем подождать.
/// </para>
/// <para>
/// Состояние «стоит замена» не хранится, а читается с движка: выбран один,
/// трафик идёт через другой. Так сюда попадает и возврат из обхода, который
/// ставит первый оживший сервер, а не выбранный.
/// </para>
/// </remarks>
public sealed class ExitWatch
{
    /// <summary>Сколько промахов подряд до замены.</summary>
    /// <remarks>
    /// Два: разовый отказ бывает и у живого сервера (решение 28.09).
    /// </remarks>
    public const int MissesBeforeReplace = 2;

    /// <summary>Сколько ответов подряд до возврата выбранного сервера.</summary>
    public const int AnswersBeforeRestore = 2;

    private readonly string? _pinned;
    private readonly bool _replacePinned;

    private int _misses;
    private int _answers;

    /// <param name="pinned">Выбранный руками сервер; <c>null</c> — автоподбор.</param>
    /// <param name="replacePinned">Можно ли подменять выбранный сервер, пока он молчит.</param>
    public ExitWatch(string? pinned, bool replacePinned)
    {
        _pinned = string.IsNullOrWhiteSpace(pinned) ? null : pinned;
        _replacePinned = replacePinned;
    }

    /// <summary>
    /// Был промах — перепроверка на ближайшем опросе, а не по расписанию.
    /// </summary>
    public bool Rechecking => _misses > 0;

    /// <summary>
    /// Стоит ли замена: сервер выбран руками, а трафик идёт через другой.
    /// </summary>
    public bool Replaced(string? current) =>
        _replacePinned && _pinned is not null && current is not null && current != _pinned;

    /// <summary>Учесть проверку подключённого выхода.</summary>
    public ExitAction OnCurrent(bool answers)
    {
        if (answers)
        {
            _misses = 0;
            return ExitAction.Keep;
        }

        // Выбран человеком, и подменять не велено — промах записан,
        // а менять выход не нам.
        if (_pinned is not null && !_replacePinned)
            return ExitAction.Keep;

        if (++_misses < MissesBeforeReplace)
            return ExitAction.Keep;

        _misses = 0;
        _answers = 0;

        return ExitAction.Replace;
    }

    /// <summary>Учесть проверку выбранного сервера, пока вместо него стоит замена.</summary>
    public ExitAction OnPinned(bool answers)
    {
        if (!answers)
        {
            _answers = 0;
            return ExitAction.Keep;
        }

        if (++_answers < AnswersBeforeRestore)
            return ExitAction.Keep;

        _answers = 0;
        _misses = 0;

        return ExitAction.Restore;
    }

    /// <summary>Забыть всё — движок перезапущен.</summary>
    public void Forget()
    {
        _misses = 0;
        _answers = 0;
    }
}
