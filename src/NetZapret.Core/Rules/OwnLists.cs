using System.Text;

namespace NetZapret.Core.Rules;

/// <summary>
/// Свои списки имён — файлы в <c>config/lists/own/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Свой домен — одна строка <c>domain: "*.example.com"</c> в rules.user.yaml,
/// и так остаётся, пока человек сам не попросит файл: ссылка «список»
/// у своего домена предлагает его завести, с названием на выбор. Тогда
/// правило переходит на hostlist этого файла, как у части сервиса, и к нему
/// можно дописать другие имена того же сайта. Убрали — убрался и файл.
/// </para>
/// <para>
/// Сам файл не заводится (владелец, 26.09): одно и то же имя в двух
/// файлах — два правила, спорящих о нём, и заводить второе без спроса
/// значит плодить спор, о котором человек не знает. Поэтому только
/// по просьбе, и окно создания называет список, с которым будет спор.
/// </para>
/// <para>
/// Папка отдельная, и это не порядок ради порядка. Файлы личные — в git
/// им нельзя (.gitignore), а build.cmd и pack.cmd копируют только
/// <c>config\lists\*.txt</c> без подпапок, так что в поставку и к движку
/// Zapret свои списки не уезжают. И по папке движок узнаёт, что список
/// свой, — см. <see cref="RuleEngine.Tier"/>.
/// </para>
/// </remarks>
public static class OwnLists
{
    /// <summary>Папка своих списков, в том виде, в каком путь пишется в правило.</summary>
    public const string Folder = "config/lists/own/";

    /// <summary>Путь списка с этим названием — значение правила.</summary>
    public static string PathFor(string name) => Folder + name + ".txt";

    /// <summary>Свой ли это список.</summary>
    public static bool IsOwn(string? value) =>
        value is not null
        && value.Replace('\\', '/').StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Название списка — имя файла без расширения.</summary>
    public static string NameOf(string value) =>
        System.IO.Path.GetFileNameWithoutExtension(value.Replace('\\', '/'));

    /// <summary>
    /// Название, годное для файла; <c>null</c> — не годится.
    /// </summary>
    /// <remarks>
    /// Буквы, цифры, точка, дефис и подчёркивание — чтобы имя файла читалось
    /// одинаково в Проводнике, в yaml и в командной строке. Пробелы
    /// становятся дефисами: «мои сайты» — законное желание, а не ошибка.
    /// </remarks>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim().ToLowerInvariant().Replace(' ', '-');

        if (trimmed.EndsWith(".txt", StringComparison.Ordinal))
            trimmed = trimmed[..^4];

        if (trimmed.Length == 0 || trimmed.Length > 64 || trimmed.Trim('.').Length == 0)
            return null;

        return trimmed.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            ? trimmed
            : null;
    }

    /// <summary>Есть ли уже список с таким названием.</summary>
    public static bool Exists(string name, string? root = null) =>
        File.Exists(Full(PathFor(name), root));

    /// <summary>
    /// Заводит список с названием <paramref name="name"/> и именами в нём.
    /// </summary>
    /// <returns>Значение правила — путь к списку.</returns>
    /// <exception cref="IOException">Список с таким названием уже есть.</exception>
    /// <remarks>
    /// Существующий не переписывается: в нём могут быть имена, дописанные
    /// руками. Имена пишутся без звёздочки — файловый список сам покрывает
    /// поддомены, у winws2 это «subdomains auto apply», у нашего
    /// сопоставления то же; прежнее «*.example.com» означало ровно это.
    /// </remarks>
    public static string Create(string name, IEnumerable<string> domains, string? root = null)
    {
        var value = PathFor(name);
        var full = Full(value, root);

        if (File.Exists(full))
            throw new IOException($"Список «{name}» уже есть.");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        var body = new StringBuilder()
            .Append($"# Свой список: {name}.\r\n")
            .Append("#\r\n")
            .Append("# Имена по одному в строке, поддомены покрываются сами.\r\n")
            .Append("# Правка действует со следующего запуска движков. Уберёте\r\n")
            .Append("# его в «Маршрутах» — удалится и этот файл.\r\n");

        foreach (var domain in domains)
            body.Append(domain.Trim().TrimStart('*', '.')).Append("\r\n");

        File.WriteAllText(full, body.ToString(), new UTF8Encoding(false));
        return value;
    }

    /// <summary>Удаляет файл своего списка; чужой путь не трогает.</summary>
    public static void Delete(string value, string? root = null)
    {
        if (!IsOwn(value))
            return;

        try
        {
            var full = Full(value, root);

            if (File.Exists(full))
                File.Delete(full);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Full(string value, string? root) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(root ?? Directory.GetCurrentDirectory(), value));
}
