using System.Net;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetZapret.Core.Rules;

/// <summary>
/// Адреса для имён, у которых своего адреса нет.
/// </summary>
/// <remarks>
/// <para>
/// Нужны для поломки, которую больше нечем лечить: имя ведёт на другое имя,
/// а у того записи A нет ни у одного резолвера. Соединяться не с чем,
/// и ни десинк, ни туннель делу не помогают — они управляют тем, как идёт
/// соединение, а не тем, куда.
/// </para>
/// <para>
/// Живой случай — <c>tr.rbxcdn.com</c>: Roblox отдаёт по нему ссылки
/// на превью, а имя оборвано. Раньше это чинилось записью в системный hosts,
/// то есть правкой чужого файла с правами администратора, переживающей
/// удаление программы. Здесь то же самое снимается одной строкой.
/// </para>
/// <para>
/// Адреса сетей доставки не вечны, поэтому файл заведомо не окончателен
/// и правится руками. Программа его только читает.
/// </para>
/// </remarks>
public sealed class AddressOverrides
{
    public static string DefaultPath => System.IO.Path.Combine("config", "addresses.yaml");

    /// <summary>Имя — адрес. Пусто, если файла нет.</summary>
    public IReadOnlyDictionary<string, string> Entries { get; }

    /// <summary>Что в файле не разобралось; для показа, а не для падения.</summary>
    public IReadOnlyList<string> Problems { get; }

    private AddressOverrides(IReadOnlyDictionary<string, string> entries, IReadOnlyList<string> problems)
    {
        Entries = entries;
        Problems = problems;
    }

    private sealed class Document
    {
        public Dictionary<string, string>? Addresses { get; set; }
    }

    /// <summary>
    /// Читает файл; отсутствие файла — не ошибка.
    /// </summary>
    /// <remarks>
    /// Ни отсутствие, ни поломка файла не мешают запуску: подстановка адресов —
    /// починка частного случая, а не условие работы. Испорченный файл называет
    /// себя в <see cref="Problems"/> и на этом успокаивается.
    /// </remarks>
    public static AddressOverrides Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        if (!File.Exists(target))
            return new AddressOverrides(entries, problems);

        try
        {
            var document = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Document>(File.ReadAllText(target));

            foreach (var (name, address) in document?.Addresses ?? [])
            {
                var host = name.Trim().TrimStart('*', '.');

                if (host.Length == 0)
                    continue;

                // Только адрес, без имён: подстановка имени вместо имени
                // вернула бы нас к тому же вопросу, ради которого всё
                // и затевалось, — а у того имени адрес есть?
                if (!IPAddress.TryParse(address?.Trim(), out var parsed))
                {
                    problems.Add($"{host}: «{address}» — не адрес");
                    continue;
                }

                entries[host] = parsed.ToString();
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{target}: {ex.GetBaseException().Message}");
        }

        return new AddressOverrides(entries, problems);
    }
}
