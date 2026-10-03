using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Пресеты в списках — самые новые сверху (владелец, 03.10).
/// </summary>
public sealed class PresetNewnessTests
{
    [Theory]
    [InlineData("Universal V11 Lite", 11)]
    [InlineData("Universal V10", 10)]
    [InlineData("Default v5 (game filter)", 5)]
    [InlineData("v3", 3)]
    [InlineData("ALL TCP & UDP multisplit_stun", null)]
    [InlineData("Dev2 тест", null)]
    [InlineData("multisplit_v2tcp", null)]
    [InlineData("Россия (RUS)", null)]
    public void Number_is_read_from_the_name(string name, int? expected)
    {
        Assert.Equal(expected, PresetNewness.Number(name));
    }

    /// <summary>
    /// Пресеты из поставки с версиями из их шапок: V11 Lite наверху,
    /// хотя у V5–V11 версия шапки одна, а у Default — выше.
    /// </summary>
    [Fact]
    public void Shipped_presets_put_the_newest_first()
    {
        (string Name, string? Version)[] presets =
        [
            ("ALL TCP & UDP multisplit_stun", "2.24"),
            ("Default v1 (game filter)", "2.40"),
            ("Default v5 (game filter)", "2.40"),
            ("Universal V10", "2.26"),
            ("Universal V11 Lite", "2.26"),
            ("Universal V5 (game filter)", "2.24"),
            ("Universal V5", "2.26"),
            ("Universal V8 diag", "2.26"),
            ("Universal V8", "2.26"),
            ("Universal V9", "2.26"),
        ];

        var ours = PresetNewness.Order(presets.Where(p => p.Name.StartsWith("Universal")), p => p.Name, p => p.Version)
            .Select(p => p.Name);

        Assert.Equal(
            ["Universal V11 Lite", "Universal V10", "Universal V9", "Universal V8", "Universal V8 diag",
             "Universal V5", "Universal V5 (game filter)"],
            ours);

        var theirs = PresetNewness.Order(presets.Where(p => !p.Name.StartsWith("Universal")), p => p.Name, p => p.Version)
            .Select(p => p.Name);

        Assert.Equal(["Default v5 (game filter)", "Default v1 (game filter)", "ALL TCP & UDP multisplit_stun"], theirs);
    }

    /// <summary>Без номера в имени решает версия шапки, как в макете владельца: Mega Lite 2.10 над Россией 2.03.</summary>
    [Fact]
    public void Without_a_number_the_header_version_decides()
    {
        (string Name, string? Version)[] presets = [("Россия (RUS)", "2.03"), ("Mega Lite", "2.10"), ("Без шапки", null)];

        Assert.Equal(
            ["Mega Lite", "Россия (RUS)", "Без шапки"],
            PresetNewness.Order(presets, p => p.Name, p => p.Version).Select(p => p.Name));
    }
}
