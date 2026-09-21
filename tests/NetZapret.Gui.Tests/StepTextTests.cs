using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Набор шагов рецепта для показа.
/// </summary>
/// <remarks>
/// Жалоба владельца 21.09: «форматирование в рецептах глянь». На снимке
/// строка <c>hostfakesplit:host=ozon.ru:tcp_ts=-1000:tcp_md5:repeats=4</c>
/// в узком окне стояла столбцом в пять букв: пробелов в ней нет, и WPF,
/// не найдя где разорвать, рвал посреди слова.
/// </remarks>
public sealed class StepTextTests
{
    private const string Zero = "​";

    [Fact]
    public void A_colon_gets_a_place_to_break_after_it()
    {
        Assert.Equal($"fake:{Zero}blob=tls_google", StepText.Breakable("fake:blob=tls_google"));
    }

    [Fact]
    public void A_comma_does_too()
    {
        // Длиннее всего именно списки: multidisorder перечисляет через
        // запятую семь позиций разреза.
        Assert.Equal($"pos=1,{Zero}host+2", StepText.Breakable("pos=1,host+2"));
    }

    [Fact]
    public void Nothing_visible_is_added()
    {
        // Ни пробелов, ни переводов строки: набор копируют в пресет,
        // и пробел там разделил бы аргумент надвое, сломав рецепт молча.
        var made = StepText.Breakable("multidisorder:pos=1,host+2:seqovl=1");

        Assert.Equal(
            "multidisorder:pos=1,host+2:seqovl=1",
            made.Replace(Zero, string.Empty));

        Assert.DoesNotContain(' ', made);
        Assert.DoesNotContain('\n', made);
    }

    [Fact]
    public void A_step_without_separators_is_left_alone()
    {
        Assert.Equal("pass", StepText.Breakable("pass"));
    }

    [Fact]
    public void Several_steps_are_joined_and_all_are_breakable()
    {
        var made = StepText.Of(["send:repeats=2", "syndata:blob=tls_google"]);

        Assert.Contains($"send:{Zero}repeats=2", made);
        Assert.Contains($"syndata:{Zero}blob=tls_google", made);
        Assert.Contains("·", made);
    }

    [Fact]
    public void An_empty_list_gives_an_empty_string()
    {
        Assert.Equal(string.Empty, StepText.Of([]));
    }

    [Fact]
    public void The_long_step_from_the_complaint_breaks_five_times()
    {
        // Тот самый набор со снимка. Пять мест переноса — по числу
        // двоеточий; прежде их было ноль, и строка ломалась где придётся.
        var made = StepText.Breakable("hostfakesplit:host=ozon.ru:tcp_ts=-1000:tcp_md5:repeats=4");

        Assert.Equal(4, made.Count(c => c == '​'));
    }
}
