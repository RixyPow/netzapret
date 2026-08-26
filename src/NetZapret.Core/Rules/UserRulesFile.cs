using System.Text;

namespace NetZapret.Core.Rules;

/// <summary>
/// Пользовательский слой правил: читается и перезаписывается программой.
/// </summary>
/// <remarks>
/// <para>
/// Отдельный файл от базового набора намеренно. Базовый обновляется вместе
/// с программой, и правки человека в нём затирались бы; кроме того, он
/// написан руками, с комментариями и осмысленным порядком, а перезапись
/// из меню всё это уничтожает.
/// </para>
/// <para>
/// Этот файл, наоборот, целиком принадлежит программе: комментариев в нём
/// не держат, порядок задаётся кодом. Записи хранятся вместе с признаком
/// включённости, чтобы выбор можно было отключить, не потеряв.
/// </para>
/// </remarks>
public sealed class UserRulesFile
{
    private readonly List<UserRuleEntry> _entries;

    private UserRulesFile(string path, List<UserRuleEntry> entries)
    {
        Path = path;
        _entries = entries;
    }

    public string Path { get; }

    public IReadOnlyList<UserRuleEntry> Entries => _entries;

    public static string DefaultPath => System.IO.Path.Combine("config", "rules.user.yaml");

    public static UserRulesFile Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        if (!File.Exists(target))
            return new UserRulesFile(target, []);

        try
        {
            // Через LoadRawRules, а не через движок: тот отбрасывает выключенные
            // правила, и они пропали бы при первом же сохранении.
            var entries = RuleSetLoader.LoadRawRules(target)
                .Select(r => new UserRuleEntry
                {
                    Match = r.Match,
                    Value = r.Value,
                    Mode = r.Mode,
                    Enabled = r.Enabled,
                })
                .ToList();

            return new UserRulesFile(target, entries);
        }
        catch (RuleConfigurationException)
        {
            // Испорченный файл не должен мешать работе: берём пустой набор,
            // сохранение перезапишет его корректным.
            return new UserRulesFile(target, []);
        }
    }

    /// <summary>
    /// Добавляет или заменяет запись. Совпадением считается пара «тип + значение».
    /// </summary>
    public void Set(MatchKind match, string value, RoutingMode mode)
    {
        var trimmed = value.Trim();
        int index = _entries.FindIndex(e => e.Match == match && e.Matches(trimmed));

        var entry = new UserRuleEntry
        {
            Match = match,
            Value = trimmed,
            Mode = mode,
            Enabled = true,
        };

        if (index >= 0)
            _entries[index] = entry;
        else
            _entries.Add(entry);
    }

    public bool Remove(MatchKind match, string value)
    {
        int index = _entries.FindIndex(e => e.Match == match && e.Matches(value.Trim()));

        if (index < 0)
            return false;

        _entries.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Удаляет запись по номеру в списке.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Remove(MatchKind, string)"/>: тот ищет по паре
    /// «тип и значение», а редактор показывает нумерованный список, и
    /// восстанавливать по номеру пару ради поиска — лишний шаг, на котором
    /// можно ошибиться.
    /// </remarks>
    public UserRuleEntry? RemoveAt(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return null;

        var removed = _entries[index];
        _entries.RemoveAt(index);
        return removed;
    }

    public bool Toggle(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return false;

        _entries[index] = _entries[index] with { Enabled = !_entries[index].Enabled };
        return true;
    }

    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var builder = new StringBuilder();

        builder.AppendLine("# Ваши маршруты.");
        builder.AppendLine("#");
        builder.AppendLine("# Файл ведёт программа: правьте его через меню или командой");
        builder.AppendLine("#   nz route <что> <напрямую|десинк|vpn>");
        builder.AppendLine("# Комментарии, добавленные вручную, при следующей записи пропадут.");
        builder.AppendLine("#");
        builder.AppendLine("# Эти правила проверяются раньше базовых из rules.yaml, поэтому");
        builder.AppendLine("# ваш выбор перекрывает заводскую настройку. Выключенная запись");
        builder.AppendLine("# сохраняется, но не применяется.");
        builder.AppendLine();

        if (_entries.Count == 0)
        {
            builder.AppendLine("rules: []");
        }
        else
        {
            builder.AppendLine("rules:");

            foreach (var entry in _entries)
            {
                builder.AppendLine($"  - match: {Describe(entry.Match)}");
                builder.AppendLine($"    value: \"{entry.Value.Replace("\"", "\\\"")}\"");
                builder.AppendLine($"    mode: {Describe(entry.Mode)}");

                if (!entry.Enabled)
                    builder.AppendLine("    enabled: false");
            }
        }

        File.WriteAllText(Path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string Describe(MatchKind match) => match switch
    {
        MatchKind.Process => "process",
        MatchKind.Domain => "domain",
        MatchKind.IpSet => "ipset",
        MatchKind.HostList => "hostlist",
        _ => "ip",
    };

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "proxy",
        RoutingMode.Desync => "desync",
        _ => "direct",
    };
}

public sealed record UserRuleEntry
{
    public required MatchKind Match { get; init; }

    public required string Value { get; init; }

    public required RoutingMode Mode { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Matches(string value) =>
        string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);

    public string DescribeMode() => Mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };

    public string DescribeMatch() => Match switch
    {
        MatchKind.Process => "программа",
        MatchKind.Domain => "домен",
        MatchKind.IpSet => "список адресов",
        MatchKind.HostList => "список доменов",
        _ => "адрес",
    };
}
