using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Переадресация на другое имя той же зоны — не ответ сайта, а куда идти дальше.
/// </summary>
/// <remarks>
/// 19.10 star-history.com отвечал 301 на www.star-history.com за 167 Б,
/// и проверка блокировок звала его доступным. Сам www замерзал на 3 261 Б:
/// владелец открыл сайт только через VPN.
/// </remarks>
public sealed class SameSiteRedirectTests
{
    [Theory]
    [InlineData("star-history.com", "https://www.star-history.com/", "www.star-history.com")]
    [InlineData("www.example.com", "https://example.com/", "example.com")]
    [InlineData("youtube.com", "https://WWW.YouTube.com/feed", "www.youtube.com")]
    public void Apex_and_www_lead_to_the_other_name(string host, string location, string expected) =>
        Assert.Equal(expected, BlockCheck.SameSiteRedirect(host, location));

    [Theory]
    [InlineData("example.com", "https://example.com/start")] // тот же хост, другой путь
    [InlineData("example.com", "/start")] // относительный — тот же хост
    [InlineData("example.com", "http://www.example.com/")] // не https
    [InlineData("example.com", "https://www.example.com:8443/")] // чужой порт
    [InlineData("x.com", "https://twitter.com/")] // чужая зона — другой сайт
    [InlineData("notexample.com", "https://example.com/")] // совпадение хвостом — не зона
    [InlineData("example.com", null)]
    public void Everything_else_is_not_followed(string host, string? location) =>
        Assert.Null(BlockCheck.SameSiteRedirect(host, location));
}
