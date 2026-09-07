using System.Diagnostics;

namespace NetZapret.Core;

/// <summary>Защитник, который вмешивается в то же, во что и мы.</summary>
public sealed record SecurityProduct
{
    /// <summary>Как его называют люди.</summary>
    public required string Name { get; init; }

    /// <summary>По какому процессу опознан — чтобы утверждение можно было проверить.</summary>
    public required string Process { get; init; }

    /// <summary>Правит ли он файл hosts за нас.</summary>
    public required bool GuardsHosts { get; init; }
}

/// <summary>
/// Ищет средства защиты, которые мешают обходу.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по случаю с живой машины: у человека Kaspersky вернул файл hosts
/// к своему умолчанию, стерев все пины, — и это было заметно только потому,
/// что он оставил записку в комментарии. Всё остальное такой защитник делает
/// молча.
/// </para>
/// <para>
/// А делает он много: свой фильтр в сетевом стеке встаёт на тот же слой WFP,
/// что и WinDivert, проверка защищённых соединений переустанавливает TLS
/// своим клиентом, а сам WinDivert помечается как <c>RiskTool</c> и может
/// уехать в карантин. Любое из трёх лишает десинк смысла, и ни одно
/// не сообщает о себе.
/// </para>
/// <para>
/// Опознание по имени процесса — приём грубый, и здесь это честно называется
/// догадкой. Служба может называться иначе в другой версии, а совпадение имени
/// не доказывает, что защита включена. Поэтому предупреждение говорит
/// «проверьте», а не «дело в нём»: программа не знает, но знает, где смотреть.
/// </para>
/// </remarks>
public static class SecuritySoftware
{
    /// <summary>
    /// Имя процесса → продукт.
    /// </summary>
    /// <remarks>
    /// Перечислены те, у кого есть собственный сетевой фильтр: обычный
    /// сканер файлов нам не мешает, и предупреждать о нём значило бы
    /// приучить пропускать предупреждение.
    /// </remarks>
    private static readonly (string Process, string Name, bool GuardsHosts)[] Known =
    [
        ("avp", "Kaspersky", true),
        ("avpui", "Kaspersky", true),
        ("kavfs", "Kaspersky", true),
        ("ekrn", "ESET", false),
        ("dwengine", "Dr.Web", true),
        ("dwservice", "Dr.Web", true),
        ("bdagent", "Bitdefender", false),
        ("vsserv", "Bitdefender", false),
        ("avastsvc", "Avast", false),
        ("avgsvc", "AVG", false),
        ("cmdagent", "Comodo", false),
    ];

    /// <summary>
    /// Что из известного работает прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Один продукт называется один раз, даже когда его процессов несколько:
    /// человеку нужно имя, а не перепись служб.
    /// </remarks>
    public static IReadOnlyList<SecurityProduct> Running()
    {
        var found = new Dictionary<string, SecurityProduct>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;

        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            // Перечисление процессов может отказать по правам. Это незнание,
            // а не отсутствие: молчим, вместо того чтобы утверждать, что чисто.
            return [];
        }

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes)
        {
            try
            {
                live.Add(process.ProcessName);
            }
            catch (Exception)
            {
                // Процесс мог завершиться между перечислением и чтением имени.
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var (name, product, guardsHosts) in Known)
        {
            if (!live.Contains(name) || found.ContainsKey(product))
                continue;

            found[product] = new SecurityProduct
            {
                Name = product,
                Process = name + ".exe",
                GuardsHosts = guardsHosts,
            };
        }

        return found.Values.ToList();
    }
}
