using NetZapret.Core.Rules;

namespace NetZapret.Tools;

/// <summary>
/// Черновик переноса: собрать книгу и сверить, ничего не записав.
/// </summary>
/// <remarks>
/// <para>
/// Перенос — самая рискованная часть работы: у владельца восемьдесят
/// восемь правил и сто двенадцать пинов, работа месяцев. Поэтому сперва
/// черновик, и смотреть его глазами, а уж потом запись.
/// </para>
/// <para>
/// Сверка не на глаз: из собранной книги собираются прежние правила
/// и сравниваются с исходными дословно. Расхождение названо поимённо —
/// восемьдесят восемь правил иначе не проверишь.
/// </para>
/// </remarks>
internal static class Migrate
{
    public static int Run(string? into)
    {
        var path = "config/rules.user.yaml";

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"не нашёл {path} — запускать из корня установки");
            return 2;
        }

        var engine = RuleSetLoader.Load(File.ReadAllText(path));
        var pins = Pins();

        Console.WriteLine($"правил: {engine.RuleSet.Rules.Count}, пинов: {pins.Count}");
        Console.WriteLine();

        var made = RouteBookMigration.From(engine.RuleSet, pins);

        foreach (var note in made.Notes)
            Console.WriteLine("! " + note);

        if (made.Notes.Count > 0)
            Console.WriteLine();

        // Сверка: собрать из книги прежние правила и сравнить дословно.
        var back = RouteBookMigration.Back(made.Book);
        var was = engine.RuleSet.Rules.Where(r => RouteBookMigration.NameOf(r) is not null).ToList();

        Console.WriteLine($"в книгу ушло:   {made.Book.Entries.Count(e => e.Choice != RouteChoice.Pin)} правил "
            + $"+ {made.Book.Entries.Count(e => e.Choice == RouteChoice.Pin)} пинов");
        Console.WriteLine($"осталось как было: {made.Kept.Count}");
        Console.WriteLine($"собралось обратно: {back.Count} из {was.Count}");
        Console.WriteLine();

        int lost = Compare(was, back);

        foreach (var clash in made.Book.Clashes)
        {
            Console.WriteLine();
            Console.WriteLine($"ПРОТИВОРЕЧИЕ  {clash.Name}");
            Console.WriteLine($"   {clash.Outcome}");
        }

        if (into is { Length: > 0 })
        {
            File.WriteAllText(into, RouteBookFile.Write(made.Book));

            Console.WriteLine();
            Console.WriteLine($"черновик записан: {into}");
            Console.WriteLine("это ЧЕРНОВИК: рабочие правила не тронуты.");
        }

        return lost == 0 ? 0 : 1;
    }

    /// <summary>
    /// Сравнивает прежние правила с собранными обратно.
    /// </summary>
    /// <remarks>
    /// По тройке «что, куда, чем»: имя списка или домена, маршрут, рецепт.
    /// Порядок при этом не сверяется — в книге он свой, — а вот состав
    /// обязан совпасть до единой записи.
    /// </remarks>
    private static int Compare(IReadOnlyList<RoutingRule> was, IReadOnlyList<RoutingRule> back)
    {
        static string Key(RoutingRule r) =>
            $"{RouteBookMigration.NameOf(r)}|{r.Mode}|{r.Recipe}";

        var before = was.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = back.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lost = before.Except(after, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();

        if (lost.Count == 0 && extra.Count == 0)
        {
            Console.WriteLine("СВЕРКА: всё сошлось дословно.");
            return 0;
        }

        foreach (var one in lost)
            Console.WriteLine($"  ПОТЕРЯНО: {one}");

        foreach (var one in extra)
            Console.WriteLine($"  ЛИШНЕЕ:   {one}");

        return lost.Count + extra.Count;
    }

    /// <summary>
    /// Наши пины из hosts — через общий разборщик, а не свой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь стоял свой разбор файла, и он тут же соврал: вместо ста
    /// двенадцати наших записей насчитал восемьсот четырнадцать — то есть
    /// забрал и чужие. Наш блок отмечен строками «&gt;&gt;&gt; netzapret
    /// begin» и «&lt;&lt;&lt; netzapret end», а моя копия искала слово
    /// «NetZapret» в любом примечании.
    /// </para>
    /// <para>
    /// Оправдание у копии было бойкое — «тянуть пробу блокировок ради
    /// разбора одного файла дорого», — и оно же есть в точности тот довод,
    /// которым заводят вторую копию логики. Час назад я записал правило:
    /// понадобилось решение — решению место в общей библиотеке. И нарушил
    /// его первым же делом, получив неверное число.
    /// </para>
    /// </remarks>
    private static IReadOnlyCollection<string> Pins() =>
        NetZapret.Proxy.HostsEditor.Pins().Keys.ToList();
}