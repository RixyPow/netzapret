using System.Text;

namespace NetZapret.Core.Updates;

/// <summary>
/// Переносит через обновление строки, которые человек дописал в наши списки.
/// </summary>
/// <remarks>
/// <para>
/// Жалоба 02.10 (EA для Apex) и обсуждение с Fint 03.10: обновление копирует
/// архив поверх <c>config\lists\</c>, и дописанные человеком имена пропадают.
/// Сами списки обновляться обязаны — починки вроде адреса превью Roblox
/// должны доезжать (<see cref="UpdateInstaller"/>), — поэтому файл целиком
/// не сохраняется, а переносится только добавленное. Владелец 03.10:
/// «сравнить файл со списком прошлой версии и сохранить добавленные строки».
/// </para>
/// <para>
/// Список прошлой версии у установки есть: <c>build.cmd</c> и <c>pack.cmd</c>
/// кладут нетронутую копию каждого нашего списка движку как
/// <c>engines\zapret\lists\nz-&lt;имя&gt;</c>, а правит человек
/// <c>config\lists\&lt;имя&gt;</c>. Строки второго, которых нет в первом, — его.
/// </para>
/// <para>
/// Удалённое человеком не переносится: о нём не просили, и вычеркнуть то,
/// что мы сами дописали в новой версии, значило бы спорить с починкой.
/// Нетронутая копия в новой поставке остаётся нетронутой — по ней сравнится
/// следующее обновление, и перенесённое переедет снова.
/// </para>
/// </remarks>
public static class ListCarry
{
    /// <summary>Пометка над перенесённым — в самом списке, чтобы человек видел, откуда строки.</summary>
    public const string Marker = "# Дописано вами до обновления — перенесено NetZapret";

    /// <summary>Приставка нетронутой копии в папке списков движка.</summary>
    public const string PristinePrefix = "nz-";

    /// <summary>
    /// Что человек дописал в каждый список: имя файла → строки в прежнем порядке.
    /// </summary>
    /// <param name="userLists">Рабочие списки — <c>config\lists</c>.</param>
    /// <param name="pristineLists">Нетронутые копии — <c>engines\zapret\lists</c>.</param>
    /// <remarks>
    /// Нет папки нетронутых копий — не знаем, с чем сравнивать, и пусто:
    /// принять весь список за правку значило бы раздуть новый файл старым.
    /// Нет копии одного файла — он не наш (человек завёл его сам), и его
    /// строки дописываются целиком, если новая версия принесёт одноимённый.
    /// Подпапки (<c>own\</c>) не смотрятся: их обновление не трогает.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Additions(string userLists, string pristineLists)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(userLists) || !Directory.Exists(pristineLists))
            return found;

        foreach (var user in Directory.EnumerateFiles(userLists, "*.txt", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(user);
            var pristine = Path.Combine(pristineLists, PristinePrefix + name);

            var shipped = File.Exists(pristine)
                ? Lines(pristine).ToHashSet(StringComparer.Ordinal)
                : [];

            var added = Lines(user)
                .Where(line => line != Marker && !shipped.Contains(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (added.Count > 0)
                found[name] = added;
        }

        return found;
    }

    /// <summary>
    /// Дописывает строки в списки новой версии; возвращает, сколько куда дописано.
    /// </summary>
    /// <param name="stagedLists">Списки распакованной новой версии — её <c>config\lists</c>.</param>
    /// <remarks>
    /// Строка, которую новая версия принесла сама, второй раз не пишется.
    /// Списка, которого в новой версии нет, не создаём: обновление его
    /// не тронет, и файл человека останется как был.
    /// </remarks>
    public static IReadOnlyDictionary<string, int> Apply(
        IReadOnlyDictionary<string, IReadOnlyList<string>> additions,
        string stagedLists)
    {
        var applied = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, lines) in additions)
        {
            var staged = Path.Combine(stagedLists, name);

            if (!File.Exists(staged))
                continue;

            var present = Lines(staged).ToHashSet(StringComparer.Ordinal);
            var missing = lines.Where(line => !present.Contains(line)).ToList();

            if (missing.Count == 0)
                continue;

            var text = File.ReadAllText(staged);
            var tail = new StringBuilder();

            if (text.Length > 0 && !text.EndsWith('\n'))
                tail.Append(Environment.NewLine);

            tail.Append(Marker).Append(Environment.NewLine);

            foreach (var line in missing)
                tail.Append(line).Append(Environment.NewLine);

            File.AppendAllText(staged, tail.ToString(), new UTF8Encoding(false));
            applied[name] = missing.Count;
        }

        return applied;
    }

    /// <summary>Значимые строки файла — без пустых и без пробелов по краям.</summary>
    private static IEnumerable<string> Lines(string path) =>
        File.ReadLines(path)
            .Select(line => line.Trim().TrimStart('﻿'))
            .Where(line => line.Length > 0);
}
