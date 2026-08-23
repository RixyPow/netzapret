using System.Text;
using System.Text.RegularExpressions;

namespace NetZapret.Core.Rules;

/// <summary>
/// Сопоставление по маске с <c>*</c> и <c>?</c>, регистронезависимое.
/// </summary>
/// <remarks>
/// Для доменов действует правило, привычное по конфигам прокси-клиентов:
/// маска <c>*.example.com</c> покрывает и сам <c>example.com</c>, потому что
/// иначе почти каждое правило приходилось бы писать двумя строками.
/// Если нужен строго поддомен — пишите <c>*.*.example.com</c> либо явный хост.
/// </remarks>
public sealed class GlobMatcher
{
    private readonly Regex _regex;
    private readonly string _pattern;

    private GlobMatcher(Regex regex, string pattern)
    {
        _regex = regex;
        _pattern = pattern;
    }

    public static GlobMatcher Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new RuleConfigurationException("Пустой шаблон в правиле.");

        var trimmed = pattern.Trim();
        var builder = new StringBuilder("^");

        // "*.example.com" -> необязательная левая часть, чтобы совпадал и голый домен.
        int start = 0;
        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            builder.Append("(?:.*\\.)?");
            start = 2;
        }

        for (int i = start; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            switch (c)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        builder.Append('$');

        var regex = new Regex(
            builder.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        return new GlobMatcher(regex, trimmed);
    }

    public bool IsMatch(string input) => _regex.IsMatch(input);

    public override string ToString() => _pattern;
}
