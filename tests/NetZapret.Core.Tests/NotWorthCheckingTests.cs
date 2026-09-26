using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Что снято с проверки — и что при этом осталось в маршрутах.
/// </summary>
/// <remarks>
/// Набор общий для консоли и окна. Держать его двумя копиями уже пробовали:
/// у окна лежал голый перечень имён, у консоли — тот же перечень с разбором,
/// и совпадать они были обязаны молча.
/// </remarks>
public sealed class NotWorthCheckingTests
{
    /// <summary>
    /// Зеркала сняты с проверки, но не из списков.
    /// </summary>
    /// <remarks>
    /// Список служит двум делам разом — правилам маршрутизации и выбору целей
    /// проверки. Вычеркнуть имя из файла ради тишины в отчёте значило бы
    /// сломать работающий маршрут: по protonmail.com до сих пор ходят почтовые
    /// клиенты, настроенные годы назад, и proton.txt прямо об этом говорит.
    /// </remarks>
    [Theory]
    [InlineData("protonmail.com")]
    [InlineData("whatsapp.com")]
    [InlineData("riotgames.es")]
    [InlineData("itch.zone")]
    [InlineData("rutor.info")]
    [InlineData("nflxvideo.net")]
    [InlineData("sndcdn.com")]
    public void A_mirror_is_dropped_from_the_check(string host)
    {
        Assert.True(NotWorthChecking.Contains(host));
    }

    /// <summary>
    /// То, ради чего зеркала и снимались, проверяться обязано.
    /// </summary>
    /// <remarks>
    /// Смысл был в том, чтобы убрать вторую строку об одном и том же, а не
    /// обе. Снять заодно и настоящее имя значило бы перестать замечать
    /// поломку вовсе — тише, но слепее.
    /// </remarks>
    [Theory]
    [InlineData("proton.me")]
    [InlineData("whatsapp.net")]
    [InlineData("riotgames.com")]
    [InlineData("itch.io")]
    [InlineData("rutracker.org")]
    [InlineData("speedtest.net")]
    [InlineData("netflix.com")]
    public void The_name_it_mirrors_is_still_checked(string host)
    {
        Assert.False(NotWorthChecking.Contains(host));
    }

    /// <summary>Сравнение без оглядки на регистр: имена приходят из файлов.</summary>
    [Fact]
    public void Case_does_not_matter()
    {
        Assert.True(NotWorthChecking.Contains("ProtonMail.COM"));
    }
}
