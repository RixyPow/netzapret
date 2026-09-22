using NetZapret.Core.Rules;

namespace NetZapret.Zapret;

/// <summary>
/// Подгружает в правила адреса из файлов-списков.
/// </summary>
/// <remarks>
/// Вынесено сюда, а не в загрузчик правил: разрешение путей требует знания
/// корня Zapret, а модель правил о существовании Zapret знать не должна.
/// </remarks>
public static class RuleSetExpander
{
    /// <summary>
    /// Загружает списки для всех правил вида <see cref="MatchKind.IpSet"/>.
    /// </summary>
    /// <returns>Описания проблем: какие списки не нашлись или оказались пусты.</returns>
    public static IReadOnlyList<string> Expand(RuleSet ruleSet, string? zapretRoot)
    {
        var problems = new List<string>();

        foreach (var rule in ruleSet.Rules.Where(r => r.Match == MatchKind.IpSet))
        {
            var cidrs = AddressListReader.Expand([rule.Value], zapretRoot, out var listProblems);

            if (listProblems.Count > 0)
            {
                problems.AddRange(listProblems.Select(p => $"правило #{rule.Ordinal}: {p}"));
                continue;
            }

            rule.LoadIpSet(cidrs);
        }

        foreach (var rule in ruleSet.Rules.Where(r => r.Match == MatchKind.HostList))
        {
            var domains = HostListReader.Read(rule.Value, zapretRoot, out var listProblems);

            if (listProblems.Count > 0)
            {
                problems.AddRange(listProblems.Select(p => $"правило #{rule.Ordinal}: {p}"));
                continue;
            }

            rule.LoadHostList(domains);
        }

        return problems;
    }

    /// <summary>
    /// Правила так, как их увидят движки при этих настройках: оба слоя,
    /// режим из выключателей, списки развёрнуты.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Одно место на окно и nz. Прежде это жило внутри окна, и инструмент
    /// разбора, отвечая «куда пойдёт имя», собирал бы правила своей копией —
    /// ровно та беда, от которой уходили вместе с консолью.
    /// </para>
    /// <para>
    /// Режим из настроек перекрывает файл правил: базовый YAML ведётся
    /// руками, и переписывать его ради режима нельзя.
    /// </para>
    /// </remarks>
    public static (RuleEngine Engine, string? ZapretRoot) LoadFor(Core.AppSettings settings)
    {
        var mode = settings.Engines.Mode;
        var loaded = RuleSetLoader.LoadLayered(settings.RulesPath, UserRulesFile.DefaultPath, mode);
        var ruleSet = loaded.RuleSet with { Operating = mode };
        var zapretRoot = ZapretPaths.Discover()?.Root;

        Expand(ruleSet, zapretRoot);

        return (new RuleEngine(ruleSet), zapretRoot);
    }
}
