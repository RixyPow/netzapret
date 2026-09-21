using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Чем объясняется неудачный рецепт.
/// </summary>
/// <remarks>
/// Жалоба владельца 21.09: «почему негативный вердикт обязательно
/// в одну строчку». Строка была одна и оттого широкая: она вытесняла
/// описание рецепта в полоску шириной в слово. Половина беды — в разметке,
/// половина — здесь: системное сообщение обрезалось по длине и давало
/// «…принудительно разорвал су».
/// </remarks>
public sealed class RecipeFailureTests
{
    [Fact]
    public void A_reset_is_named_a_reset()
    {
        // Так отвечает DPI, оборвавший рукопожатие, — самый частый исход
        // у неподходящего рецепта.
        Assert.Equal(
            "соединение сброшено",
            RecipeWindow.Short(new SocketException((int)SocketError.ConnectionReset)));
    }

    [Fact]
    public void A_reset_wrapped_in_something_else_is_found_too()
    {
        // SslStream заворачивает сокетную ошибку в IOException, и до неё
        // GetBaseException доходит не всегда.
        var wrapped = new IOException(
            "оболочка",
            new SocketException((int)SocketError.ConnectionReset));

        Assert.Equal("соединение сброшено", RecipeWindow.Short(wrapped));
    }

    [Fact]
    public void A_refused_handshake_is_told_apart_from_a_reset()
    {
        // Разные вещи: сброс — это вмешался кто-то третий, отказ —
        // сторона не приняла наше приветствие. Лечатся разным.
        Assert.Equal(
            "отказ в рукопожатии",
            RecipeWindow.Short(new AuthenticationException("что угодно")));
    }

    [Fact]
    public void Silence_is_named_silence()
    {
        Assert.Equal("не дождались", RecipeWindow.Short(new OperationCanceledException()));
        Assert.Equal("не дождались", RecipeWindow.Short(new TimeoutException()));

        Assert.Equal(
            "не дождались",
            RecipeWindow.Short(new SocketException((int)SocketError.TimedOut)));
    }

    [Fact]
    public void Nothing_comes_out_longer_than_a_line()
    {
        // Ради этого всё и делалось: длинное системное сообщение требовало
        // всю свою ширину и вытесняло описание рецепта.
        var сообщения = new Exception[]
        {
            new SocketException((int)SocketError.ConnectionReset),
            new AuthenticationException("длинное сообщение от платформы"),
            new IOException(new string('а', 300)),
            new InvalidOperationException("Удаленный хост принудительно разорвал существующее подключение"),
        };

        Assert.All(сообщения, ex => Assert.True(
            RecipeWindow.Short(ex).Length <= 41,
            $"слишком длинно: {RecipeWindow.Short(ex)}"));
    }

    [Fact]
    public void An_unknown_failure_is_cut_at_a_visible_place()
    {
        // Обрезка осталась для незнакомого, но теперь видно, что обрезали:
        // прежде строка просто кончалась посреди слова.
        var cut = RecipeWindow.Short(new InvalidOperationException(new string('б', 200)));

        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void A_short_message_is_left_whole()
    {
        Assert.Equal("коротко", RecipeWindow.Short(new InvalidOperationException("коротко")));
    }
}
