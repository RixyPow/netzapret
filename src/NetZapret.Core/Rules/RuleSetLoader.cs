using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetZapret.Core.Rules;

/// <summary>
/// Загружает набор правил из YAML (или JSON — YAML является его надмножеством).
/// </summary>
public static class RuleSetLoader
{
    public static RuleEngine LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new RuleConfigurationException($"Файл конфига не найден: {path}");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new RuleConfigurationException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }

        try
        {
            return Load(text);
        }
        catch (RuleConfigurationException ex)
        {
            throw new RuleConfigurationException($"{path}: {ex.Message}", ex);
        }
    }

    public static RuleEngine Load(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        RuleFileDto? dto;
        try
        {
            dto = deserializer.Deserialize<RuleFileDto>(yaml);
        }
        catch (Exception ex) when (ex is not RuleConfigurationException)
        {
            throw new RuleConfigurationException($"YAML не разбирается: {ex.Message}", ex);
        }

        if (dto is null)
            throw new RuleConfigurationException("Конфиг пуст.");

        var rules = new List<RoutingRule>();
        var rawRules = dto.Rules ?? new List<RuleDto>();

        for (int i = 0; i < rawRules.Count; i++)
        {
            var raw = rawRules[i];

            if (string.IsNullOrWhiteSpace(raw.Value))
                throw new RuleConfigurationException($"Правило #{i}: не задано поле 'value'.");

            rules.Add(new RoutingRule
            {
                Match = ParseMatchKind(raw.Match, i),
                Value = raw.Value.Trim(),
                Mode = ParseMode(raw.Mode, $"правило #{i}"),
                Server = string.IsNullOrWhiteSpace(raw.Server) ? null : raw.Server.Trim(),
                Ordinal = i,
            });
        }

        var operating = ParseOperatingMode(dto.Mode);

        // Если default не задан явно, он вытекает из глобального режима: в
        // selective «всё остальное» — это десинк, а не прямой проход. Иначе
        // пользователю пришлось бы дублировать выбор режима в двух местах.
        var defaultMode = dto.Default?.Mode is { } explicitMode
            ? ParseMode(explicitMode, "секция 'default'")
            : operating switch
            {
                OperatingMode.ProxyAll => RoutingMode.Proxy,
                OperatingMode.Selective => RoutingMode.Desync,
                _ => RoutingMode.Direct,
            };

        var defaultServer = string.IsNullOrWhiteSpace(dto.Default?.Server) ? null : dto.Default!.Server!.Trim();

        var engine = RuleEngine.Build(rules, defaultMode, defaultServer, operating);

        return dto.Capture is { Count: > 0 }
            ? new RuleEngine(engine.RuleSet with { CaptureEntries = dto.Capture })
            : engine;
    }

    private static OperatingMode ParseOperatingMode(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "off" or "disabled" => OperatingMode.Off,
            "selective" or "desync" => OperatingMode.Selective,
            "proxy" or "proxy_all" or "vpn" => OperatingMode.ProxyAll,
            "" => OperatingMode.Selective,
            var other => throw new RuleConfigurationException(
                $"Неизвестный режим работы '{other}'. Допустимы: off, selective, proxy."),
        };

    private static MatchKind ParseMatchKind(string? value, int index) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "process" => MatchKind.Process,
            "domain" => MatchKind.Domain,
            "ip" or "cidr" => MatchKind.Ip,
            "" => throw new RuleConfigurationException($"Правило #{index}: не задано поле 'match'."),
            var other => throw new RuleConfigurationException(
                $"Правило #{index}: неизвестный тип совпадения '{other}'. Допустимы: process, domain, ip."),
        };

    private static RoutingMode ParseMode(string? value, string context) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "direct" => RoutingMode.Direct,
            "proxy" => RoutingMode.Proxy,
            "desync" or "zapret" => RoutingMode.Desync,
            "" => throw new RuleConfigurationException($"{context}: не задано поле 'mode'."),
            var other => throw new RuleConfigurationException(
                $"{context}: неизвестный режим '{other}'. Допустимы: direct, proxy, desync."),
        };

    private sealed class RuleFileDto
    {
        /// <summary>Глобальный режим работы: off | selective | proxy.</summary>
        public string? Mode { get; set; }

        public List<RuleDto>? Rules { get; set; }
        public DefaultDto? Default { get; set; }

        /// <summary>Что дополнительно заводить в туннель: CIDR или пути к спискам.</summary>
        public List<string>? Capture { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Match { get; set; }
        public string? Value { get; set; }
        public string? Mode { get; set; }
        public string? Server { get; set; }
    }

    private sealed class DefaultDto
    {
        public string? Mode { get; set; }
        public string? Server { get; set; }
    }
}
