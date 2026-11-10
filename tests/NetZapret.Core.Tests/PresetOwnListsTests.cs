using System.Text.RegularExpressions;
using NetZapret.Core;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Наши списки в пресетах: на каждый <c>lists/nz-*.txt</c> есть файл в config\lists.
/// </summary>
/// <remarks>
/// <para>
/// Файлы <c>nz-*</c> рядом с движком кладут build.cmd и pack.cmd, копируя
/// config\lists. Ссылку на список, которого там нет, winws2 переживает молча:
/// секция остаётся в пресете и не совпадает ни с чем. V10 завёл два новых
/// списка — virustotal и bsky-app — и забытый файл выглядел бы ровно так.
/// </para>
/// <para>
/// Проверяется исходник в config\lists, а не собранный бандл: бандла
/// в рабочей копии может не быть, а без исходника его не будет и в поставке.
/// </para>
/// </remarks>
public sealed class PresetOwnListsTests
{
    private static readonly Regex OwnList = new(@"lists/nz-(?<name>[^\s""]+\.txt)", RegexOptions.Compiled);

    private static readonly Regex OwnLua = new(@"^--lua-init=@lua/nz-(?<name>[^\s""]+\.lua)", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void EveryOwnListAPresetNamesHasASource()
    {
        if (Repository() is not { } root)
            return;

        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "presets"), "*.txt"))
        {
            foreach (Match match in OwnList.Matches(File.ReadAllText(file)))
            {
                var source = Path.Combine(root, "config", "lists", match.Groups["name"].Value);

                if (!File.Exists(source))
                    missing.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(missing.Count == 0, "Нет исходника в config\\lists: " + string.Join(", ", missing.Distinct()));
    }

    /// <summary>
    /// Наши модули lua в пресетах: на каждый <c>@lua/nz-*.lua</c> есть исходник в config\lua,
    /// и оба скрипта сборки его доставляют.
    /// </summary>
    /// <remarks>
    /// С модулем строже, чем со списком: без файла списка секция молча не совпадает ни с чем,
    /// а без файла из <c>--lua-init</c> winws2 не поднимается вовсе — и видно это только
    /// при запуске движков, сухой прогон lua не читает (10.11, «Universal V13 YT FIX»).
    /// </remarks>
    [Fact]
    public void EveryOwnLuaModuleAPresetNamesHasASourceAndIsBundled()
    {
        if (Repository() is not { } root)
            return;

        var missing = new List<string>();
        bool any = false;

        foreach (var file in Directory.GetFiles(Path.Combine(root, "presets"), "*.txt"))
        {
            foreach (Match match in OwnLua.Matches(File.ReadAllText(file)))
            {
                any = true;

                if (!File.Exists(Path.Combine(root, "config", "lua", match.Groups["name"].Value)))
                    missing.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(missing.Count == 0, "Нет исходника в config\\lua: " + string.Join(", ", missing.Distinct()));

        if (!any)
            return;

        foreach (var script in new[] { "build.cmd", "pack.cmd" })
        {
            Assert.Contains(
                @"config\lua\*.lua",
                File.ReadAllText(Path.Combine(root, script)),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Основной пресет лежит в presets\ — иначе первый запуск идёт без десинка.</summary>
    [Fact]
    public void TheDefaultPresetShips()
    {
        if (Repository() is not { } root)
            return;

        var path = ZapretPaths.FindPreset(AppSettings.DefaultPresetName, Path.Combine(root, "presets"));

        Assert.NotNull(path);
        Assert.Equal(AppSettings.DefaultPresetName, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// В секциях, выбранных вручную, имена — файлом: строка
    /// <c>--hostlist-domains</c> поддоменов не покрывает.
    /// </summary>
    [Fact]
    public void HandPickedSectionsOfTheDefaultPresetUseFiles()
    {
        if (Repository() is not { } root)
            return;

        var path = ZapretPaths.FindPreset(AppSettings.DefaultPresetName, Path.Combine(root, "presets"));
        Assert.NotNull(path);

        var picked = new PresetReader().Load(path!).Sections
            .Where(s => s.Name.StartsWith("Выбрано вручную:", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(picked);

        foreach (var section in picked)
        {
            Assert.DoesNotContain(section.RawArguments, a => a.StartsWith("--hostlist-domains=", StringComparison.Ordinal));
            Assert.Contains(section.RawArguments, a => a.StartsWith("--hostlist=lists/nz-", StringComparison.Ordinal));
        }
    }

    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
