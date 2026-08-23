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

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Value(string key) => _values.GetValueOrDefault(key);

    public string Value(string key, string fallback) => _values.GetValueOrDefault(key) ?? fallback;

    public int Int(string key, int fallback) =>
        int.TryParse(_values.GetValueOrDefault(key), out var value) ? value : fallback;
}
