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

    /// <summary>
    /// Записывает свой домен — зоной «*.имя», «напрямую».
    /// </summary>
    /// <returns>Значение правила («*.example.com»); <c>null</c> — вписанное не похоже на имя сайта.</returns>
    /// <remarks>
    /// <para>
    /// Жило в «Маршрутах», в поле поиска. С 11.11 свой домен заводит и поле
    /// «Сайт не открывается?» на «Главной», и правило должно выходить одно
    /// и то же.
    /// </para>
    /// <para>
    /// Звёздочка ставится сама: имя означает зону, и <c>example.com</c> должен
    /// покрывать поддомены — иначе картинки с <c>cdn.example.com</c> пойдут мимо
    /// правила. По той же причине снимается «www.»: «*.www.example.com» не
    /// покрыло бы сам сайт. «Напрямую», а не выбранное заранее (владелец, 23.09):
    /// режим, рецепт и пин правятся в строке части — там же, где у всех.
    /// </para>
    /// </remarks>
    public static string? AddOwnDomain(string? text, string? path = null)
    {
        if (DomainInput.Normalize(text) is not { } name)
            return null;

        if (name.StartsWith("www.", StringComparison.Ordinal) && name.IndexOf('.', 4) > 0)
            name = name[4..];

        var value = "*." + name;
        var file = Load(path);

        file.Set(MatchKind.Domain, value, RoutingMode.Direct, recipe: null);
        file.Save();

        return value;
    }

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
                    Recipe = r.Recipe,
                    Server = r.Mode == RoutingMode.Proxy ? r.Server : null,
                    Enabled = r.Enabled,
                    Off = r.Off,
                    Special = r.Special,
                    Pin = r.Pin,
                    PinHosts = r.PinHosts,
                })
                .ToList();

            return new UserRulesFile(target, entries);
        }
        catch (RuleConfigurationException)
        {
            // Испорченный файл не должен мешать работе, но и пропадать
            // не должен: следующее сохранение перезаписало бы его пустым,
            // и правки человека исчезли бы вместе с опечаткой, из-за которой
            // файл не разобрался. Отодвигаем в сторону — починить или
            // подсмотреть можно, работе не мешает.
            SetAside(target);
            return new UserRulesFile(target, []);
        }
    }

    /// <summary>Расширение, под которым сохраняется неразобранный файл.</summary>
    public const string BrokenSuffix = ".broken";

    private static void SetAside(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var broken = path + BrokenSuffix;

            // Затираем предыдущий сохранённый: две поломки подряд означают,
            // что чинят прямо сейчас, и интересна последняя.
            if (File.Exists(broken))
                File.Delete(broken);

            File.Move(path, broken);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Добавляет или заменяет запись. Совпадением считается пара «тип + значение».
    /// </summary>
    /// <param name="recipe">
    /// Чем чинить имя при <see cref="RoutingMode.Desync"/>; <c>null</c> —
    /// решает пресет, как было до появления выбора.
    /// </param>
    /// <param name="before">
    /// Значение более широкого правила того же типа, раньше которого встаёт
    /// новая запись. Часть сервиса, лежащая внутри другого списка
    /// (<c>ServicePart.Within</c>), иначе встала бы после него и не сработала
    /// никогда: движок берёт первое совпавшее правило.
    /// </param>
    /// <param name="server">
    /// Куда в VPN при <see cref="RoutingMode.Proxy"/>: пул (<see cref="ServerPools.RuleServer"/>);
    /// <c>null</c> — пул по умолчанию.
    /// </param>
    /// <remarks>
    /// Пометку особого маршрута (<see cref="UserRuleEntry.Special"/>) запись
    /// сохраняет, пока режим остаётся тем, каким особый маршрут работает: смена
    /// рецепта у пина сервиса пин не снимает. Другой режим — другой путь,
    /// и пометка уходит.
    /// </remarks>
    public void Set(MatchKind match, string value, RoutingMode mode, string? recipe = null, string? before = null,
        string? server = null)
    {
        var was = _entries.FirstOrDefault(e => e.Match == match && e.Matches(value.Trim()));
        bool keep = was is { Off: false } && was.Special != RouteSpecial.None && RouteSpecials.ModeOf(was.Special) == mode;

        Put(new UserRuleEntry
        {
            Match = match,
            Value = value.Trim(),
            Mode = mode,

            // Рецепт хранится только у десинка: у прочих режимов ему нечего
            // означать, а оставленный при смене режима он всплыл бы потом
            // необъяснимой строкой в файле. Пул — так же, только у VPN.
            Recipe = mode == RoutingMode.Desync ? recipe : null,
            Server = mode == RoutingMode.Proxy && !string.IsNullOrWhiteSpace(server) ? server.Trim() : null,
            Enabled = true,
            Special = keep ? was!.Special : RouteSpecial.None,
            Pin = keep ? was!.Pin : null,
            PinHosts = keep ? was!.PinHosts : UserRuleEntry.NoHosts,
        }, before);
    }

    /// <summary>
    /// Записывает особый маршрут: режим — тот, каким он работает, и что ради него поставлено.
    /// </summary>
    /// <param name="recipe">Рецепт десинка — у пина сервиса и прокси Telegram; <c>null</c> — пресет.</param>
    /// <param name="pin">Чей пин: решение из pin-solutions.yaml или источник адреса.</param>
    /// <param name="hosts">Имена и адреса, прибитые ради маршрута; у пина сервиса не нужны.</param>
    public void SetSpecial(MatchKind match, string value, RouteSpecial special, string? recipe = null, string? pin = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? hosts = null, string? before = null)
    {
        if (special == RouteSpecial.None)
            throw new ArgumentException("Особый маршрут не задан.", nameof(special));

        var mode = RouteSpecials.ModeOf(special);

        Put(new UserRuleEntry
        {
            Match = match,
            Value = value.Trim(),
            Mode = mode,
            Recipe = mode == RoutingMode.Desync ? recipe : null,
            Enabled = true,
            Special = special,
            Pin = string.IsNullOrWhiteSpace(pin) ? null : pin.Trim(),
            PinHosts = hosts is null
                ? UserRuleEntry.NoHosts
                : hosts.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
        }, before);
    }

    /// <summary>
    /// Меняет адреса пина в записях, которые его держат, — за автозаменой пинов.
    /// </summary>
    /// <returns>Сколько записей изменилось.</returns>
    /// <remarks>
    /// Автозамена (<c>PinRefresh</c>) переводит имя на новый адрес посредника
    /// раз в 6 часов. Без этого запись в файле врала бы об адресе с первой же
    /// замены — а по ней читают отчёт и ставят пины чужой машины.
    /// </remarks>
    public int Repin(IReadOnlyDictionary<string, IReadOnlyList<string>> moved)
    {
        int changed = 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];

            if (entry.Special != RouteSpecial.RelayPin
                || !entry.PinHosts.Any(p => moved.TryGetValue(p.Key, out var now) && now.Count > 0
                    && !now.SequenceEqual(p.Value, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var hosts = entry.PinHosts.ToDictionary(
                p => p.Key,
                p => moved.TryGetValue(p.Key, out var now) && now.Count > 0 ? now : p.Value,
                StringComparer.OrdinalIgnoreCase);

            _entries[i] = entry with { PinHosts = hosts };
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Переписывает адреса пинов в файле маршрутов за автозаменой; не прочиталось — молча.
    /// </summary>
    /// <returns>Сколько записей изменилось.</returns>
    public static int RepinSaved(IReadOnlyDictionary<string, IReadOnlyList<string>> moved, string? path = null)
    {
        try
        {
            var file = Load(path);
            int changed = file.Repin(moved);

            if (changed > 0)
                file.Save();

            return changed;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Выключает часть: не действует ни своё правило, ни базовое на тот же список.
    /// </summary>
    /// <remarks>См. <see cref="RoutingRule.Off"/>. Место в файле — как у <see cref="Set"/>.</remarks>
    public void SetOff(MatchKind match, string value, string? before = null) =>
        Put(new UserRuleEntry
        {
            Match = match,
            Value = value.Trim(),
            Mode = RoutingMode.Direct,
            Enabled = true,
            Off = true,
        }, before);

    private void Put(UserRuleEntry entry, string? before)
    {
        int index = _entries.FindIndex(e => e.Match == entry.Match && e.Matches(entry.Value));

        if (index >= 0)
        {
            _entries[index] = entry;
            return;
        }

        int wider = before is null ? -1 : _entries.FindIndex(e => e.Match == entry.Match && e.Matches(before.Trim()));

        if (wider >= 0)
            _entries.Insert(wider, entry);
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

    /// <summary>
    /// Переставляет запись: вынимает с места <paramref name="from"/>
    /// и ставит на место <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Для перетаскивания в «Порядке вычисления» (владелец, 26.09). Внутри
    /// своей группы движок проверяет правила в порядке файла, и прежде
    /// поменять его можно было только руками — а файл ведёт программа.
    /// </para>
    /// <para>
    /// Порядок переживает дальнейшие правки: <see cref="Set"/> заменяет
    /// запись на её же месте, а новая встаёт в конец.
    /// </para>
    /// </remarks>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _entries.Count || to < 0 || to >= _entries.Count)
            return false;

        if (from == to)
            return true;

        var entry = _entries[from];
        _entries.RemoveAt(from);
        _entries.Insert(to, entry);
        return true;
    }

    /// <summary>Заменяет запись на её же месте.</summary>
    public void ReplaceAt(int index, UserRuleEntry entry) => _entries[index] = entry;

    /// <summary>Заменяет все записи чужими — загрузка маршрутов из файла (<see cref="RulesShare"/>).</summary>
    public void ReplaceAll(IEnumerable<UserRuleEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
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
        builder.AppendLine("# Файл ведёт программа: правьте его на вкладке «Маршруты».");
        builder.AppendLine("# Комментарии, добавленные вручную, при следующей записи пропадут.");
        builder.AppendLine("#");
        builder.AppendLine("# Эти правила проверяются раньше базовых из rules.yaml, поэтому");
        builder.AppendLine("# ваш выбор перекрывает заводскую настройку. Выключенная запись");
        builder.AppendLine("# (enabled: false) сохраняется, но не применяется — действует");
        builder.AppendLine("# заводская. mode: off выключает часть целиком: не действует");
        builder.AppendLine("# ни эта запись, ни заводское правило на тот же список.");
        builder.AppendLine("#");
        builder.AppendLine("# special — особый маршрут: service-pin (пин на адреса самого сервиса,");
        builder.AppendLine("# с десинком), relay-pin (пин на посредника, напрямую), tg-proxy");
        builder.AppendLine("# (Telegram через свой прокси). pin — чей пин, pin_hosts — какие имена");
        builder.AppendLine("# к каким адресам прибиты в hosts. Сами пины стоят в hosts; загрузка");
        builder.AppendLine("# этого файла на другой машине ставит их по этим строкам.");
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
                builder.AppendLine($"    value: {Quote(entry.Value)}");
                builder.AppendLine($"    mode: {(entry.Off ? "off" : Describe(entry.Mode))}");

                if (!entry.Off && entry.Mode == RoutingMode.Proxy && !string.IsNullOrWhiteSpace(entry.Server))
                    builder.AppendLine($"    server: {Quote(entry.Server)}");

                if (!string.IsNullOrWhiteSpace(entry.Recipe))
                    builder.AppendLine($"    recipe: {Quote(entry.Recipe)}");

                if (!entry.Off && RouteSpecials.Word(entry.Special) is { } special)
                {
                    builder.AppendLine($"    special: {special}");

                    if (!string.IsNullOrWhiteSpace(entry.Pin))
                        builder.AppendLine($"    pin: {Quote(entry.Pin)}");

                    if (entry.PinHosts.Count > 0)
                    {
                        builder.AppendLine("    pin_hosts:");

                        foreach (var (host, addresses) in entry.PinHosts.OrderBy(p => p.Key, StringComparer.Ordinal))
                            builder.AppendLine($"      {Quote(host)}: [{string.Join(", ", addresses.Select(Quote))}]");
                    }
                }

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

    /// <summary>Строка YAML в двойных кавычках — с экранированием и косой черты.</summary>
    /// <remarks>
    /// Прежде экранировалась одна кавычка. Путь программы вида
    /// <c>C:\Program Files\App\app.exe</c> YAML читает как управляющие
    /// последовательности <c>\P</c>, <c>\A</c> — и отвергает файл целиком:
    /// одно правило по программе стоило бы всех своих маршрутов. Нашлось
    /// 23.09 на тесте, где список лёг путём Windows.
    /// </remarks>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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

    /// <summary>Чем чинить имя; <c>null</c> — решает пресет.</summary>
    public string? Recipe { get; init; }

    /// <summary>
    /// Куда в VPN: пул (<see cref="ServerPools.RuleServer"/>) или тег сервера;
    /// <c>null</c> — пул по умолчанию.
    /// </summary>
    /// <remarks>
    /// Поле правила было и прежде (<see cref="RoutingRule.Server"/>), но файл
    /// своих маршрутов его не хранил: до пулов выбрать сервер маршруту было
    /// негде, и при первой же записи файла оно пропало бы.
    /// </remarks>
    public string? Server { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Часть выключена целиком (<see cref="RoutingRule.Off"/>); <see cref="Mode"/> тогда не значит ничего.</summary>
    public bool Off { get; init; }

    /// <summary>Особый маршрут (<see cref="RouteSpecial"/>); <see cref="Mode"/> — режим, которым он работает.</summary>
    public RouteSpecial Special { get; init; }

    /// <summary>Чей пин: решение из pin-solutions.yaml или источник адреса.</summary>
    public string? Pin { get; init; }

    /// <summary>Имена и адреса, прибитые ради маршрута.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PinHosts { get; init; } = NoHosts;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> NoHosts { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public bool Matches(string value) =>
        string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);

    public string DescribeMode() => Off ? "выключено"
        : Special != RouteSpecial.None ? RouteSpecials.Describe(Special, Pin)
        : Mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };

}
