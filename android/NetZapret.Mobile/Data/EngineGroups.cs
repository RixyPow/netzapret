using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetZapret.Mobile.Data;

/// <summary>Член группы движка: сервер или вложенная группа, с последним замером.</summary>
/// <param name="Delay">Мс последнего замера; <c>null</c> — не мерился, 0 и меньше движок так и отдаёт.</param>
internal sealed record GroupItem(string Tag, string Type, int? Delay);

internal sealed record EngineGroup(string Tag, string Type, string? Selected, IReadOnlyList<GroupItem> Items);

/// <summary>
/// Группы движка по ответу хозяина на команду <c>groups</c> (android/DESIGN.md §4).
/// </summary>
/// <remarks>
/// Селектор, через который идёт туннель, — <c>auto</c> (<c>SingBoxOptions.SelectorTag</c>);
/// в нём группа автоподбора <c>auto-latency</c> и все серверы. «Авто» на
/// вкладке VPN — селектор на группе автоподбора, сервер — селектор прямо
/// на нём, как у окна Windows через Clash API.
/// </remarks>
internal static class EngineGroups
{
    public const string Selector = "auto";

    public const string AutoPick = "auto-latency";

    /// <summary>У libbox провал замера — это предельная задержка, а не ноль.</summary>
    private const int Failed = 65535;

    public static IReadOnlyList<EngineGroup> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
                return [];

            var groups = new List<EngineGroup>();

            foreach (var node in array.OfType<JsonObject>())
            {
                var items = (node["items"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(i => new GroupItem(
                        (string?)i["tag"] ?? string.Empty,
                        (string?)i["type"] ?? string.Empty,
                        i["delay"] is JsonValue d && d.TryGetValue<int>(out var ms) && ms > 0 ? ms : null))
                    .Where(i => i.Tag.Length > 0)
                    .ToList();

                groups.Add(new EngineGroup(
                    (string?)node["tag"] ?? string.Empty,
                    (string?)node["type"] ?? string.Empty,
                    (string?)node["selected"],
                    items));
            }

            return groups;
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Задержки серверов из всех групп: тег → мс; провал замера — <c>-1</c>.</summary>
    public static IReadOnlyDictionary<string, int> Delays(IReadOnlyList<EngineGroup> groups)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in groups.SelectMany(g => g.Items))
        {
            if (item.Delay is { } ms)
                result[item.Tag] = ms >= Failed ? -1 : ms;
        }

        return result;
    }

    /// <summary>Через что идёт трафик и сам ли это выбрал автоподбор.</summary>
    public static (string? Server, bool Automatic) Current(IReadOnlyList<EngineGroup> groups)
    {
        var selector = groups.FirstOrDefault(g => g.Tag == Selector);

        if (selector?.Selected is not { Length: > 0 } selected)
            return (null, true);

        // Селектор стоит на группе — спрашиваем её саму, кого она держит.
        var nested = groups.FirstOrDefault(g => g.Tag == selected);

        if (nested is not null)
            return (nested.Selected, true);

        return (selected, false);
    }

    /// <summary>Есть ли ответ об ошибке: <c>{"error":"…"}</c>.</summary>
    public static string? Error(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
            return null;

        try
        {
            return JsonNode.Parse(json)?["error"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string SelectArgument(string group, string outbound) =>
        new JsonObject { ["group"] = group, ["outbound"] = outbound }.ToJsonString();

    public static string GroupArgument(string group) =>
        new JsonObject { ["group"] = group }.ToJsonString();
}
