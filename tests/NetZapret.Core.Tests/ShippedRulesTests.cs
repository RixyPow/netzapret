using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Поставляемые файлы правил разбираются.
/// </summary>
/// <remarks>
/// <para>
/// Опечатка в YAML не ломает компиляцию, но делает программу неработоспособной
/// у всех, кто её скачал. Прежде это проверял шаг CI, гонявший консольную
/// команду rules по каждому config/*.yaml. Консоль уходит, и проверка
/// переехала сюда — тем же вызовом, которым rules и читала файл.
/// </para>
/// <para>
/// Здесь она вдобавок гоняется локально, до сборки, а не только в CI.
/// </para>
/// </remarks>
public sealed class ShippedRulesTests
{
    private static string? Config()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return Path.Combine(directory.FullName, "config");
        }

        return null;
    }

    [Fact]
    public void Every_shipped_yaml_parses()
    {
        var config = Config();
        if (config is null)
            return;

        // rules.user.yaml — личный файл владельца, в репозиторий не входит
        // и проверке не подлежит: у каждого он свой.
        var files = Directory.GetFiles(config, "*.yaml")
            .Where(f => !Path.GetFileName(f).Equals("rules.user.yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(files);

        var broken = new List<string>();

        foreach (var file in files)
        {
            try
            {
                RuleSetLoader.LoadFromFile(file);
            }
            catch (Exception ex)
            {
                broken.Add($"{Path.GetFileName(file)}: {ex.GetBaseException().Message}");
            }
        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    [Fact]
    public void The_base_rules_are_not_empty()
    {
        // Разобраться в ноль правил — тоже поломка, и молчаливая:
        // программа работает, только ничего никуда не ведёт.
        var config = Config();
        if (config is null)
            return;

        Assert.NotEmpty(RuleSetLoader.LoadFromFile(Path.Combine(config, "rules.yaml")).RuleSet.Rules);
    }
}
