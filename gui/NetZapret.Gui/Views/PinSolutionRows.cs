using System.Windows;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>Строка решения с пином в «Настройках маршрутов».</summary>
public sealed record PinSolutionRow
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Caption { get; init; }

    public required string Info { get; init; }

    public required string Word { get; init; }

    public required bool On { get; init; }

    public required bool Available { get; init; }

    public Visibility LineShown { get; init; } = Visibility.Visible;

    /// <summary>Недоступное решение бледнее — как у Zapret GUI.</summary>
    public double Fade => Available ? 1.0 : 0.55;
}

/// <summary>
/// Строки раздела «Пины напрямую».
/// </summary>
/// <remarks>
/// Отдельно от окна по той же причине, что <see cref="CatalogRows"/>: это
/// единственная его чистая часть, и проверять тестом надо её, а не копию.
/// </remarks>
internal static class PinSolutionRows
{
    /// <param name="haveIpV6"><c>null</c> — ещё не проверено.</param>
    public static IReadOnlyList<PinSolutionRow> Build(
        IReadOnlyList<PinSolution> solutions,
        bool? haveIpV6,
        Func<PinSolution, PinSolutionState> stateOf)
    {
        var rows = new List<PinSolutionRow>();

        foreach (var solution in solutions)
        {
            var state = stateOf(solution);

            // Пока IPv6 не проверен, решение «только на IPv6» не предлагаем:
            // включить его вслепую значит прописать адреса, до которых нет дороги.
            string? unavailable = haveIpV6 is { } known
                ? PinSolutions.Unavailable(solution, known)
                : solution.NeedsIpV6 ? "Проверяю, есть ли IPv6…" : null;

            // Включённое снять можно всегда — даже если IPv6 с тех пор пропал.
            bool available = unavailable is null || state.On;

            var caption = unavailable is not null && !state.On
                ? unavailable
                : state.Partly
                    ? $"{solution.Note} Прибито {state.Pinned} из {state.Total} имён."
                    : solution.Note ?? string.Empty;

            if (state.Elsewhere > 0)
                caption += $" Своих пинов на его имена: {state.Elsewhere} — их выключатель не тронет.";

            rows.Add(new PinSolutionRow
            {
                Id = solution.Id,
                Title = solution.Name,
                Caption = caption.Trim(),
                Info = Describe(solution),
                Word = !state.On ? "выкл." : state.Partly ? "частично" : "вкл.",
                On = state.On,
                Available = available,
                LineShown = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
            });
        }

        return rows;
    }

    /// <summary>Что будет прибито: несколько имён и адресов — чтобы видеть, к чему.</summary>
    private static string Describe(PinSolution solution)
    {
        var names = solution.Names;
        var addresses = solution.Pins.SelectMany(p => p.Addresses).Distinct().ToList();

        var text = $"Имён: {names.Count} — {string.Join(", ", names.Take(3))}"
            + (names.Count > 3 ? " и другие" : string.Empty)
            + $". Адресов: {addresses.Count} — {string.Join(", ", addresses.Take(3))}"
            + (addresses.Count > 3 ? " и другие" : string.Empty) + ".";

        text += solution.Desync
            ? "\n\nАдреса самого сервиса: строки в hosts получают пометку «# nz:desync», и десинк к ним применяется. Работает, когда часть сервиса стоит на «десинке»; на «напрямую» десинка не будет, «через VPN» — пин уйдёт в туннель."
            : "\n\nАдреса посредников: десинк к ним не применяется — рецепт рвёт с посредником соединение.";

        if (solution.NeedsIpV6)
            text += " Работает, только если в сети есть IPv6.";

        return text + "\n\nИсточник — каталог hosts Zapret GUI, раздел «Напрямую».";
    }
}
