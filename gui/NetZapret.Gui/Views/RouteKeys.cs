using NetZapret.Core.Rules;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Ключ строки в списке маршрутов.
/// </summary>
/// <remarks>
/// <para>
/// Вид <c>род|значение</c>. Родов три: <c>hostlist</c> и <c>ipset</c> —
/// части сервисов из каталога, значение у них путь к файлу списка;
/// <c>own</c> — свой домен, значение у него само правило вида
/// <c>*.example.com</c>.
/// </para>
/// <para>
/// Ключ один на все действия над строкой: пин, снятие пина, выбор
/// маршрута, выбор рецепта, удаление. Разбирается он отсюда, а не
/// по месту, потому что мест этих шесть, и в каждом решение одно и то же —
/// какое правило писать и где искать зоны. Расхождение между ними
/// не проявилось бы падением: правило легло бы не того рода, и «убрать»
/// потом не нашло бы того, что записано.
/// </para>
/// <para>
/// Отдельно от <c>RoutesView</c> затем, что это его единственная чистая
/// часть. Разбор ключа проверяется без окна, а обработчик нажатия — нет.
/// </para>
/// </remarks>
internal static class RouteKeys
{
    /// <summary>Свой домен, добавленный человеком.</summary>
    public const string Own = "own";

    /// <summary>Часть сервиса, заданная подсетями.</summary>
    public const string IpSet = "ipset";

    /// <summary>Часть сервиса, заданная именами.</summary>
    public const string HostList = "hostlist";

    public static string Make(string kind, string value) => kind + "|" + value;

    /// <summary>Разобрать ключ; <c>null</c> — вид не тот.</summary>
    public static (string Kind, string Value)? Parse(string? key) =>
        key?.Split('|', 2) is [var kind, var value] && kind.Length > 0
            ? (kind, value)
            : null;

    /// <summary>Каким правилом записывается маршрут строки.</summary>
    /// <remarks>
    /// Свой домен с 26.09 — файл списка (<see cref="OwnLists"/>), и правило
    /// на него hostlist, как у части сервиса. Но только когда значение и
    /// вправду путь своего списка: голое имя прежнего вида остаётся
    /// domain-правилом. Hostlist на «*.example.com» ссылался бы на файл,
    /// которого нет, и не совпал бы ни с чем — молча.
    /// </remarks>
    public static MatchKind MatchOf(string kind, string value) => kind switch
    {
        IpSet => MatchKind.IpSet,
        Own when !OwnLists.IsOwn(value) => MatchKind.Domain,
        _ => MatchKind.HostList,
    };

    /// <summary>
    /// Спрашивается ли для этой строки рецепт десинка.
    /// </summary>
    /// <remarks>
    /// У правил по адресам не спрашивается: рецепт применяется по имени
    /// в приветствии TLS, а в таких пакетах имени нет вовсе — любой рецепт
    /// отвечал бы «не помогает» независимо от собственных достоинств.
    /// </remarks>
    public static bool TakesRecipe(string kind) => kind is HostList or Own;

    /// <summary>
    /// Зоны, которыми задана строка.
    /// </summary>
    /// <remarks>
    /// У каталожной части читаются из файла списка, у своего домена он сам
    /// себе зона — файла у него нет. Нужны трижды: пометить пин, снять его
    /// и увести маршрут напрямую. Три разошедшиеся ветки «если свой домен»
    /// дали бы три разных ответа на один вопрос, и самый тихий из них —
    /// пустой список зон, то есть «пина нет» над живым пином.
    /// </remarks>
    public static IReadOnlyList<string> Zones(string key, string? zapretRoot)
    {
        if (Parse(key) is not var (kind, value))
            return [];

        // Свой домен прежнего вида — само правило, файла у него нет.
        // Такие переводятся в списки при открытии «Маршрутов», но ключ
        // мог прийти раньше.
        if (kind == Own && !OwnLists.IsOwn(value))
            return [value.TrimStart('*', '.')];

        try
        {
            return HostListReader.Read(value, zapretRoot, out _)
                .Select(d => d.TrimStart('*', '.'))
                .ToList();
        }
        catch (Exception)
        {
            // Нечитаемый список — не повод ронять раздел. Пустые зоны
            // означают «пина не видно», и это честнее сорванной вкладки.
            return [];
        }
    }

    /// <summary>
    /// Покрывает ли какая-нибудь зона это прибитое имя.
    /// </summary>
    /// <remarks>
    /// По зоне, а не по точному совпадению: списки хранят <c>openai.com</c>,
    /// а прибивается <c>api.openai.com</c>. При точном сравнении кнопка
    /// врала бы «пина нет» над живым пином.
    /// </remarks>
    public static bool Covers(IReadOnlyList<string> zones, string name) =>
        zones.Any(zone => string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase));
}
