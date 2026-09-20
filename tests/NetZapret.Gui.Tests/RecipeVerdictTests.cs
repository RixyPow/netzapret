using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Что окно выбора рецепта считает успехом и как об этом говорит.
/// </summary>
/// <remarks>
/// <para>
/// Жалоба владельца 21.09: «по какому принципу этот подбор решает, что
/// доступ есть? Инста нихера не грузит». Прежнее правило звучало так:
/// пришёл хоть один байт до закрытия — «открывается за 0,8 с». Instagram
/// на <c>GET /</c> отвечает коротким перенаправлением на страницу входа,
/// и этого хватало.
/// </para>
/// <para>
/// Правило новое: успехом считается разобранный код ответа. Ответивший
/// сервер означает, что до него дошли — ни фильтр, ни заглушка внятного
/// HTTP по чужому имени не отдадут. Но перенаправление называется
/// перенаправлением, а не страницей.
/// </para>
/// </remarks>
public sealed class RecipeVerdictTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(0.8);

    private static RecipeWindow.Flow Answer(int status, int bytes, string? location = null) => new()
    {
        Bytes = bytes,
        Status = status,
        Location = location,
        Ending = "сервер закрыл соединение",
    };

    [Fact]
    public void A_real_page_is_a_success()
    {
        var flow = Answer(200, 21756);

        Assert.True(flow.Passed);
        Assert.True(flow.Page);
        Assert.Contains("страница 200", RecipeWindow.Verdict(flow, Second));
    }

    [Fact]
    public void A_redirect_that_led_nowhere_is_not_an_opening()
    {
        // Ровно тот случай, с которого началось, и уже во второй раз.
        // Сперва 301 звался «открывается», потом «фильтр пройден» — и
        // владелец справедливо сказал: «результат не изменился, только
        // обёртка другая». Теперь перенаправления проходятся до конца,
        // а недоведённое в счёт открывших не идёт вовсе.
        var flow = Answer(302, 431, "https://elsewhere.example/login")
            with { Ending = "уводит на elsewhere.example" };

        Assert.True(flow.Passed);
        Assert.False(flow.Page);

        var verdict = RecipeWindow.Verdict(flow, Second);

        Assert.Contains("страницы нет", verdict);
        Assert.Contains("elsewhere.example", verdict);
    }

    [Fact]
    public void A_loop_of_redirects_is_a_failure_too()
    {
        var flow = Answer(301, 431, "https://example.com/")
            with { Ending = "перенаправления не кончаются" };

        Assert.False(flow.Page);
        Assert.Contains("не кончаются", RecipeWindow.Verdict(flow, Second));
    }

    [Fact]
    public void Bytes_without_a_status_line_are_not_a_success()
    {
        // Прежнее правило — «пришло хоть что-то» — сюда и проваливалось.
        // Обрывок, по которому сказать ничего нельзя, успехом не считается.
        var flow = new RecipeWindow.Flow
        {
            Bytes = 512,
            Ending = "512 Б, и это не похоже на HTTP",
        };

        Assert.False(flow.Passed);
        Assert.StartsWith("не помогает", RecipeWindow.Verdict(flow, Second));
    }

    [Fact]
    public void Silence_says_so()
    {
        var flow = RecipeWindow.Flow.Nothing("тишина");

        Assert.False(flow.Passed);
        Assert.Contains("тишина", RecipeWindow.Verdict(flow, Second));
    }

    [Fact]
    public void A_stream_killed_partway_says_where()
    {
        // «Оборвалось на 3069 Б» и «тишина» лечатся разным, а прежде
        // оба выглядели одинаковым «не помогает».
        var flow = RecipeWindow.Flow.Nothing("оборвалось на 3069 Б");

        Assert.False(flow.Passed);
        Assert.Contains("3069", RecipeWindow.Verdict(flow, Second));
    }

    [Theory]
    [InlineData(200, true, true)]
    [InlineData(204, true, true)]
    [InlineData(301, true, false)]
    [InlineData(302, true, false)]
    [InlineData(403, false, false)]
    [InlineData(500, false, false)]
    public void Where_the_line_is_drawn(int status, bool passed, bool page)
    {
        // 4xx и 5xx фильтр тоже прошли бы — сервер ответил, — но отличить
        // отказ сайта от отказа фильтра по одному коду нельзя, и обещать
        // рабочий рецепт по ним неправильно. Пусть считаются неудачей:
        // ошибка в эту сторону стоит лишнего перебора, а в обратную —
        // выбранного рецепта, который ничего не чинит.
        var flow = Answer(status, 900);

        Assert.Equal(passed, flow.Passed);
        Assert.Equal(page, flow.Page);
    }

    [Fact]
    public void A_page_reached_through_redirects_counts()
    {
        // Instagram отвечает 301 на apex и 200 на www. Довести до страницы
        // и значит ответить на вопрос, ради которого проверку и открывают.
        var flow = Answer(200, 21756);

        Assert.True(flow.Page);
        Assert.Contains("страница 200", RecipeWindow.Verdict(flow, Second));
    }
}
