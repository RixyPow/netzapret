using NetZapret.Core;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Поиск «Что нужно сделать?» в окне.</summary>
public sealed class TaskSearchTests
{
    private sealed record Item(string Text, string Extra);

    private static readonly Item[] Items =
    [
        new("Добавить подписку VPN", "VPN ключ vless сервер"),
        new("Выбрать сервер или страну", "VPN выход"),
        new("Пустить сайт через VPN", "Маршруты домен правило"),
        new("Сменить DNS", "DNS резолвер doh"),
        new("Посмотреть пины в hosts", "Файл hosts"),
        new("Подключить Telegram через прокси", "TG Proxy"),
        new("Замерить скорость", "Замер скорости интернет"),
    ];

    private static IReadOnlyList<string> Find(string query) =>
        TaskSearch.Find(Items, i => i.Text, i => i.Extra, query).Select(i => i.Text).ToList();

    [Fact]
    public void Empty_query_returns_everything_in_order()
    {
        Assert.Equal(Items.Select(i => i.Text), Find("  "));
    }

    [Fact]
    public void Word_endings_do_not_matter()
    {
        Assert.Contains("Добавить подписку VPN", Find("подписка"));
        Assert.Contains("Выбрать сервер или страну", Find("серверы"));
        Assert.Contains("Замерить скорость", Find("скорости"));
    }

    [Fact]
    public void Every_word_must_be_found()
    {
        Assert.Equal(["Добавить подписку VPN"], Find("добавить vpn"));
        Assert.Empty(Find("добавить телеграм"));
    }

    [Fact]
    public void People_words_find_window_words()
    {
        Assert.Contains("Подключить Telegram через прокси", Find("тг"));
        Assert.Contains("Подключить Telegram через прокси", Find("телеграм"));
        Assert.Contains("Сменить DNS", Find("днс"));
        Assert.Contains("Посмотреть пины в hosts", Find("хосты"));
        Assert.Contains("Добавить подписку VPN", Find("впн"));
    }

    /// <summary>Найденное в тексте задачи — выше найденного только в ключевых словах.</summary>
    [Fact]
    public void Matches_in_the_task_itself_come_first()
    {
        var found = Find("vpn");

        Assert.Equal("Добавить подписку VPN", found[0]);
        Assert.Equal("Пустить сайт через VPN", found[1]);
        Assert.Contains("Выбрать сервер или страну", found);
    }

    [Fact]
    public void Yo_and_case_are_ignored()
    {
        Assert.Equal(["Сменить DNS"], Find("СМЕНИТЬ dns"));
        Assert.Equal(TaskSearch.Words("Ёлка"), TaskSearch.Words("елка"));
    }
}
