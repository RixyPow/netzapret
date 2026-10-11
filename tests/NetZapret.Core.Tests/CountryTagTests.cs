using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Флаг из имени сервера — значком, а не буквами в тексте (Windows флаги не рисует).
/// </summary>
public sealed class CountryTagTests
{
    [Theory]
    [InlineData("🇩🇪 Германия 🚀", "DE", "Германия 🚀")]
    [InlineData("tg:VLESSFORU 🇵🇱 ⚡", "PL", "tg:VLESSFORU ⚡")]
    [InlineData("LTE/5G Все операторы 🇪🇺", "EU", "LTE/5G Все операторы")]
    [InlineData("⚡🇫🇮 Финляндия", "FI", "⚡ Финляндия")]
    [InlineData("🇷🇺 → 🇩🇪 мост", "DE", "🇷🇺 → мост")]
    [InlineData("Без флага", "", "Без флага")]
    [InlineData("🇩", "", "🇩")]
    public void FlagAnywhereBecomesTheCountry(string tag, string country, string name)
    {
        var (foundCountry, foundName) = CountryTag.Split(tag);

        Assert.Equal(country, foundCountry);
        Assert.Equal(name, foundName);
    }
}
