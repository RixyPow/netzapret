using System.Text;

namespace NetZapret.Core.Rules;

/// <summary>
/// Свои домены — файлами списков, как у сервисов.
/// </summary>
/// <remarks>
/// <para>
/// Просьба владельца 26.09. Прежде свой домен был одной строкой
/// <c>domain: "*.example.com"</c> в rules.user.yaml: файла у него не было,
/// и дописать к нему второе имя того же сайта можно было только вторым
/// своим доменом. Теперь у каждого свой файл в <c>config/lists/own/</c>,
/// правило на него — hostlist, как у частей сервисов, и файл открывается
/// ссылкой «список» и правится руками. Убрали домен — убрался и файл.
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

    /// <summary>Путь списка для домена — значение правила.</summary>
    public static string PathFor(string domain) => Folder + domain.Trim().TrimStart('*', '.').ToLowerInvariant() + ".txt";

    /// <summary>Свой ли это список.</summary>
    public static bool IsOwn(string? value) =>
        value is not null
        && value.Replace('\\', '/').StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Домен, ради которого список заведён, — по имени файла.</summary>
    public static string DomainOf(string value) =>
        System.IO.Path.GetFileNameWithoutExtension(value.Replace('\\', '/'));

    /// <summary>
    /// Заводит файл списка с одним именем; существующий не трогает.
    /// </summary>
    /// <remarks>
    /// Имя пишется без звёздочки: файловый список сам покрывает поддомены —
    /// у winws2 это «subdomains auto apply», у нашего сопоставления то же.
    /// Прежнее правило «*.example.com» означало ровно это.
    /// </remarks>
    /// <param name="root">Корень установки; <c>null</c> — текущий каталог.</param>
    public static string Create(string domain, string? root = null)
    {
        var value = PathFor(domain);
        var full = Full(value, root);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        if (!File.Exists(full))
        {
            var name = DomainOf(value);

            File.WriteAllText(full,
                $"# Свой домен: {name}.\r\n"
                + "#\r\n"
                + "# Файл завела программа, когда вы добавили домен в «Маршрутах».\r\n"
                + "# Допишите сюда другие имена того же сайта — по одному в строке,\r\n"
                + "# поддомены покрываются сами. Удалите домен в «Маршрутах» —\r\n"
                + "# удалится и этот файл.\r\n"
                + name + "\r\n",
                new UTF8Encoding(false));
        }

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

    /// <summary>
    /// Переводит свои домены прежнего вида в списки.
    /// </summary>
    /// <returns>Сколько записей переведено; 0 — файл не менялся.</returns>
    /// <remarks>
    /// <para>
    /// Каждая <c>domain</c>-запись становится hostlist-записью на своё место:
    /// режим, рецепт и выключенность сохраняются, порядок тоже — внутри
    /// группы он решает, какое правило побеждает.
    /// </para>
    /// <para>
    /// Файл уже есть — запись переводится на него, файл не переписывается:
    /// его могли дополнить руками. Сохранять — дело вызывающего.
    /// </para>
    /// </remarks>
    public static int Migrate(UserRulesFile file, string? root = null)
    {
        int moved = 0;

        for (int i = 0; i < file.Entries.Count; i++)
        {
            var entry = file.Entries[i];

            if (entry.Match != MatchKind.Domain)
                continue;

            var path = Create(entry.Value, root);

            file.ReplaceAt(i, entry with { Match = MatchKind.HostList, Value = path });
            moved++;
        }

        return moved;
    }

    private static string Full(string value, string? root) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(root ?? Directory.GetCurrentDirectory(), value));
}
