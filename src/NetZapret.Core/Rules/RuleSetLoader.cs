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

    /// <summary>
    /// Загружает базовый набор и накладывает поверх пользовательский.
    /// </summary>
    /// <param name="basePath">Выверенный набор: то, что заблокировано наверняка.</param>
    /// <param name="userPath">Выбор пользователя; может отсутствовать.</param>
    /// <remarks>
    /// Два файла, а не один: базовый обновляется вместе с программой, и правки
    /// человека в нём затирались бы. Разделение позволяет обновлять первый,
    /// не трогая второй.
    /// </remarks>
    /// <param name="mode">
    /// Режим из настроек. Перекрывает записанный в файле.
    /// </param>
    /// <remarks>
    /// <para>
    /// Параметр появился поздно и по нужде. Комментарий ниже с самого начала
    /// утверждал, что «общие настройки живут в меню», — но передать их сюда
    /// было нечем, и режим всегда брался из <c>rules.yaml</c>, где стоит
    /// <c>selective</c>. Человек переключал «всё через VPN», а правила
    /// вычислялись по-прежнему.
    /// </para>
    /// <para>
    /// Умолчание пересчитывается вместе с режимом. Иначе «всё через VPN»
    /// оставляло бы десинк тем, что не покрыто правилами, — то есть
    /// большинству.
    /// </para>
    /// </remarks>
    public static RuleEngine LoadLayered(string basePath, string? userPath, OperatingMode? mode = null)
    {
        var baseEngine = LoadFromFile(basePath);

        if (userPath is null || !File.Exists(userPath))
            return mode is null ? baseEngine : WithMode(baseEngine, mode.Value);

        var userEngine = LoadFromFile(userPath);

        foreach (var rule in userEngine.RuleSet.Rules)
            rule.Source = RuleSource.User;

        var merged = userEngine.RuleSet.Rules.Concat(baseEngine.RuleSet.Rules).ToList();

        var operating = mode ?? baseEngine.RuleSet.Operating;

        // Режим и умолчание берутся из базового: пользовательский файл описывает
        // только отдельные маршруты, общие настройки живут в меню.
        return RuleEngine.Build(
            merged,
            mode is null ? baseEngine.RuleSet.DefaultMode : DefaultFor(operating),
            baseEngine.RuleSet.DefaultServer,
            operating)
            .WithCapture(baseEngine.RuleSet.CaptureEntries
                .Concat(userEngine.RuleSet.CaptureEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>Пересобирает движок под другой режим.</summary>
    private static RuleEngine WithMode(RuleEngine engine, OperatingMode mode) =>
        RuleEngine.Build(
            engine.RuleSet.Rules.ToList(),
            DefaultFor(mode),
            engine.RuleSet.DefaultServer,
            mode)
            .WithCapture(engine.RuleSet.CaptureEntries.ToList());

    /// <summary>Куда идёт то, что не покрыто ни одним правилом.</summary>
    private static RoutingMode DefaultFor(OperatingMode mode) => mode switch
    {
        OperatingMode.ProxyAll or OperatingMode.ProxyStrict => RoutingMode.Proxy,
        OperatingMode.Selective or OperatingMode.DesyncOnly => RoutingMode.Desync,
        _ => RoutingMode.Direct,
    };

    /// <summary>
    /// Разбирает правила из файла, не отбрасывая выключенные.
    /// </summary>
    /// <remarks>
    /// Нужен редактору пользовательского слоя: движок выключенные правила
    /// отбрасывает, и файл-модель, читая через него, теряла бы их при
    /// первом же сохранении.
    /// </remarks>
    public static IReadOnlyList<RoutingRule> LoadRawRules(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<RoutingRule>();

        try
        {
            return ParseRules(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            throw new RuleConfigurationException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<RoutingRule> ParseRules(string yaml) => ParseFile(yaml).Rules;

    public static RuleEngine Load(string yaml)
    {
        var parsed = ParseFile(yaml);

        var engine = RuleEngine.Build(
            parsed.Rules,
            parsed.DefaultMode,
            parsed.DefaultServer,
            parsed.Operating);

        return parsed.Capture.Count > 0 ? engine.WithCapture(parsed.Capture) : engine;
    }

    private readonly record struct ParsedFile(
        IReadOnlyList<RoutingRule> Rules,
        RoutingMode DefaultMode,
        string? DefaultServer,
        OperatingMode Operating,
        IReadOnlyList<string> Capture);

    private static ParsedFile ParseFile(string yaml)
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
                Enabled = raw.Enabled ?? true,
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
                OperatingMode.ProxyAll or OperatingMode.ProxyStrict => RoutingMode.Proxy,
                OperatingMode.Selective or OperatingMode.DesyncOnly => RoutingMode.Desync,
                _ => RoutingMode.Direct,
            };

        var defaultServer = string.IsNullOrWhiteSpace(dto.Default?.Server) ? null : dto.Default!.Server!.Trim();

        return new ParsedFile(
            rules,
            defaultMode,
            defaultServer,
            operating,
            dto.Capture ?? (IReadOnlyList<string>)Array.Empty<string>());
    }

    private static OperatingMode ParseOperatingMode(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "off" or "disabled" => OperatingMode.Off,
            "selective" => OperatingMode.Selective,
            "desync" or "desync_only" => OperatingMode.DesyncOnly,
            "proxy" or "proxy_all" or "vpn" => OperatingMode.ProxyAll,
            "proxy_strict" or "vpn_only" => OperatingMode.ProxyStrict,
            "" => OperatingMode.Selective,
            var other => throw new RuleConfigurationException(
                $"Неизвестный режим работы '{other}'. Допустимы: off, selective, " +
                "desync, proxy, proxy_strict."),
        };

    private static MatchKind ParseMatchKind(string? value, int index) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "process" => MatchKind.Process,
            "domain" => MatchKind.Domain,
            "ip" or "cidr" => MatchKind.Ip,
            "ipset" or "iplist" => MatchKind.IpSet,
            "hostlist" or "domainlist" => MatchKind.HostList,
            "" => throw new RuleConfigurationException($"Правило #{index}: не задано поле 'match'."),
            var other => throw new RuleConfigurationException(
                $"Правило #{index}: неизвестный тип совпадения '{other}'. " +
                "Допустимы: process, domain, ip, ipset, hostlist."),
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

        /// <summary>Выключенное правило хранится, но не применяется.</summary>
        public bool? Enabled { get; set; }
    }

    private sealed class DefaultDto
    {
        public string? Mode { get; set; }
        public string? Server { get; set; }
    }
}
