using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверка Cloudflare на робота — не отказ по стране.
/// </summary>
/// <remarks>
/// Замер 23.09: openai.com и claude.ai через пин в hosts отвечали 403
/// с <c>cf-mitigated: challenge</c> и страницей «Just a moment…», с узла
/// во Франкфурте. Проба звала это «сайт отказывает по стране», а ChatGPT
/// в браузере работал: браузер проверку проходит, проба — нет.
/// </remarks>
public sealed class BotChallengeTests
{
    [Fact]
    public void CloudflareChallengeIsRecognised()
    {
        var bytes = Encoding.ASCII.GetBytes(
            "HTTP/1.1 403 Forbidden\r\nServer: cloudflare\r\nCf-Mitigated: challenge\r\nCF-RAY: a3f8-FRA\r\n\r\n");

        Assert.Equal(403, BlockCheck.ParseStatus(bytes, bytes.Length));
        Assert.True(BlockCheck.IsBotChallenge(bytes, bytes.Length));
    }

    /// <summary>Обычный 403 так и остаётся отказом.</summary>
    [Fact]
    public void PlainForbiddenIsNotAChallenge()
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nServer: cloudflare\r\nContent-Length: 10\r\n\r\n");

        Assert.False(BlockCheck.IsBotChallenge(bytes, bytes.Length));
    }

    /// <summary>
    /// Метка ищется в заголовках, а не в теле: страница может сама
    /// рассказывать о проверках.
    /// </summary>
    [Fact]
    public void BodyMentionDoesNotCount()
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\n\r\ncf-mitigated: challenge");

        Assert.False(BlockCheck.IsBotChallenge(bytes, bytes.Length));
    }
}
