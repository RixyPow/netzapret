namespace NetZapret.Cli;

/// <summary>
/// Разбор аргументов вида <c>--ключ значение</c> и <c>--флаг</c>.
/// </summary>
/// <remarks>
/// Своя реализация вместо System.CommandLine: у той до сих пор нет стабильного
/// релиза, а API между preview-версиями менялся. Для трёх команд с горсткой
/// опций внешняя зависимость того не стоит.
/// </remarks>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command, IReadOnlyList<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positional { get; }

    public static CommandLine Parse(string[] args)
    {
        string command = "help";
        int start = 0;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = args[0];
            start = 1;
        }

        var positional = new List<string>();
        var result = new CommandLine(command, positional);

        for (int i = start; i < args.Length; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var key = arg[2..];
            var eq = key.IndexOf('=');

            if (eq >= 0)
            {
                result._values[key[..eq]] = key[(eq + 1)..];
                continue;
            }

            // Следующий токен — значение, если он сам не выглядит опцией.
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            result._values[key] = hasValue ? args[++i] : null;
        }

        return result;
    }

    private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string key)
    {
        _read.Add(key);
        return _values.ContainsKey(key);
    }

    public string? Value(string key)
    {
        _read.Add(key);
        return _values.GetValueOrDefault(key);
    }

    public string Value(string key, string fallback)
    {
        _read.Add(key);
        return _values.GetValueOrDefault(key) ?? fallback;
    }

    public int Int(string key, int fallback)
    {
        _read.Add(key);
        return int.TryParse(_values.GetValueOrDefault(key), out var value) ? value : fallback;
    }

    /// <summary>
    /// Переданные опции, которых команда не спрашивала.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Неизвестная опция раньше молча отбрасывалась, и команда делала не то,
    /// о чём её просили, ничем этого не выдавая. На <c>preset --name</c>
    /// вместо <c>--preset</c> попался автор этих строк: команда невозмутимо
    /// вывалила весь список вместо запрошенного пресета, и время ушло
    /// на поиск несуществующей неисправности.
    /// </para>
    /// <para>
    /// Учитываются прочитанные ключи, а не перечень допустимых для каждой
    /// команды: такой перечень пришлось бы держать в двух местах и однажды
    /// забыть обновить.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> UnusedOptions() =>
        _values.Keys.Where(k => !_read.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
}
