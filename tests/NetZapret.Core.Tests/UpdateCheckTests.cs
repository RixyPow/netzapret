using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сравнение версий.
/// </summary>
/// <remarks>
/// Сеть здесь не участвует: запрос к GitHub проверяется живым запуском,
/// а разбор номера — вот этим. Ошибка тут дороже, чем кажется: неверное
/// сравнение либо не покажет вышедшую починку, либо предложит откат.
/// </remarks>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.3.2", "0.3.1", true)]
    [InlineData("0.4.0", "0.3.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.3.1", "0.3.1", false)]
    [InlineData("0.3.0", "0.3.1", false)]
    public void ComparesByNumber(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateCheck.IsNewer(candidate, current));
    }

    /// <summary>Числами, а не строками.</summary>
    /// <remarks>
    /// Как строка «0.10.0» меньше «0.9.0», и посимвольное сравнение спрятало бы
    /// от человека десятый выпуск подряд — ровно тогда, когда накопится больше
    /// всего починок.
    /// </remarks>
    [Fact]
    public void TenIsNewerThanNine()
    {
        Assert.True(UpdateCheck.IsNewer("0.10.0", "0.9.0"));
        Assert.False(UpdateCheck.IsNewer("0.9.0", "0.10.0"));
    }

    [Fact]
    public void LeadingVeeIsAccepted()
    {
        Assert.True(UpdateCheck.IsNewer("v0.3.2", "0.3.1"));
        Assert.True(UpdateCheck.IsNewer("0.3.2", "v0.3.1"));
    }

    /// <summary>Мусор не считается новее.</summary>
    /// <remarks>
    /// Ошибиться в эту сторону безопаснее: не показать обновление досадно,
    /// а предложить установить неизвестно что — уже опасно.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("не версия")]
    public void UnparsableIsNotNewer(string candidate)
    {
        Assert.False(UpdateCheck.IsNewer(candidate, "0.3.1"));
    }

    /// <summary>Настройки и правила не перезаписываются обновлением.</summary>
    /// <remarks>
    /// Перечислены поимённо, а не оставлены на усмотрение сборщика архива:
    /// обновление, стирающее подписку, хуже отсутствия обновления, и связывать
    /// два сценария молчаливым уговором значит однажды его нарушить.
    /// </remarks>
    [Fact]
    public void SettingsAreNeverOverwritten()
    {
        var names = UpdateInstaller.Preserved.Select(Path.GetFileName).ToList();

        Assert.Contains("netzapret.json", names);
        Assert.Contains("rules.user.yaml", names);
        Assert.Contains("addresses.yaml", names);
    }
}
