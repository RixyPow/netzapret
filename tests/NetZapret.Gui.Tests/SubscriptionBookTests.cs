using System.IO;
using NetZapret.Core;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Список подписок: чтение, перенос старых настроек, выбор действующей.
/// </summary>
/// <remarks>
/// Половина этого файла про совместимость с тем, что уже стоит у людей:
/// ссылка, живущая только в <c>netzapret.json</c>, и WARP, успевший побыть
/// отдельной строкой списка. Ошибка здесь не роняет ничего — она молча
/// отключает работающий туннель, и это хуже.
/// </remarks>
public sealed class SubscriptionBookTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"netzapret-book-{Guid.NewGuid():N}");

    private readonly string _was = Directory.GetCurrentDirectory();

    public SubscriptionBookTests()
    {
        Directory.CreateDirectory(_root);

        // Load читает и настройки по пути по умолчанию — значит текущий
        // каталог должен быть своим, иначе тест прочтёт настройки хозяина
        // машины и, того хуже, перепишет их.
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_was);

        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private static void Settings(AppSettings settings) => settings.Save(AppSettings.DefaultPath);

    /// <summary>
    /// Ссылка из настроек подтягивается в список.
    /// </summary>
    /// <remarks>
    /// У тех, кто настроился до появления этого файла, подписка живёт только
    /// в <c>netzapret.json</c>. Без переноса раздел VPN показал бы пустоту
    /// человеку с работающим туннелем.
    /// </remarks>
    [Fact]
    public void The_active_url_from_settings_appears_in_the_list()
    {
        Settings(new AppSettings { SubscriptionUrl = "https://panel.example/sub" });

        var book = SubscriptionBook.Load();

        Assert.Single(book.Entries);
        Assert.Equal("https://panel.example/sub", book.Entries[0].Url);
        Assert.Equal("Основная", book.Entries[0].Name);
    }

    /// <summary>И подтягивается один раз, а не при каждом чтении.</summary>
    [Fact]
    public void An_url_already_in_the_list_is_not_added_twice()
    {
        Settings(new AppSettings { SubscriptionUrl = "https://panel.example/sub" });

        new SubscriptionBook
        {
            Entries = [new SubscriptionEntry { Name = "Своя", Url = "https://panel.example/sub" }],
        }.Save();

        var book = SubscriptionBook.Load();

        Assert.Single(book.Entries);
        Assert.Equal("Своя", book.Entries[0].Name);
    }

    /// <summary>Строки без ссылки выбрасываются при чтении.</summary>
    /// <remarks>
    /// Появиться такая может от правки файла руками или от прерванного
    /// добавления. Оставшись, она выглядит подпиской, которая не работает.
    /// </remarks>
    [Fact]
    public void An_entry_without_an_url_is_dropped()
    {
        Settings(new AppSettings());

        new SubscriptionBook
        {
            Entries =
            [
                new SubscriptionEntry { Name = "Пустая", Url = "   " },
                new SubscriptionEntry { Name = "Живая", Url = "https://panel.example/sub" },
            ],
        }.Save();

        Assert.Single(SubscriptionBook.Load().Entries);
    }

    /// <summary>
    /// WARP переносится из списка в выключатель.
    /// </summary>
    /// <remarks>
    /// Отдельной строкой он оказался вреден: конфиг собирается по одной
    /// ссылке, и выбор его выхода делал действующим его, отключая рабочую
    /// подписку целиком.
    /// </remarks>
    [Fact]
    public void A_warp_entry_becomes_the_switch()
    {
        Settings(new AppSettings { SubscriptionUrl = "https://panel.example/sub" });

        new SubscriptionBook
        {
            Entries =
            [
                new SubscriptionEntry { Name = "Основная", Url = "https://panel.example/sub" },
                new SubscriptionEntry { Name = "WARP", Url = "warp://own" },
            ],
        }.Save();

        var book = SubscriptionBook.Load();

        Assert.Single(book.Entries);
        Assert.True(AppSettings.Load(AppSettings.DefaultPath).WarpEnabled);
    }

    /// <summary>
    /// Если действующим был WARP, указатель переезжает на живую подписку.
    /// </summary>
    /// <remarks>
    /// Иначе он повис бы на ссылке, которой больше нет, и раздел показал бы
    /// «ни одна не действует» при живых подписках.
    /// </remarks>
    [Fact]
    public void An_active_warp_hands_the_baton_over()
    {
        Settings(new AppSettings { SubscriptionUrl = "warp://own" });

        new SubscriptionBook
        {
            Entries =
            [
                new SubscriptionEntry { Name = "WARP", Url = "warp://own" },
                new SubscriptionEntry { Name = "Основная", Url = "https://panel.example/sub" },
            ],
        }.Save();

        var book = SubscriptionBook.Load();
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        Assert.True(settings.WarpEnabled);
        Assert.Equal("https://panel.example/sub", settings.SubscriptionUrl);
        Assert.NotNull(book.Active(settings));
    }

    /// <summary>
    /// Смена подписки сбрасывает выбранный сервер.
    /// </summary>
    /// <remarks>
    /// Прежний выбор принадлежал прошлой подписке, и в новой такого тега
    /// может не быть вовсе. Оставленный, он превращается в ссылку в никуда:
    /// конфиг собирается без выбранного выхода, а окно уверяет, что выход
    /// выбран.
    /// </remarks>
    [Fact]
    public void Switching_the_subscription_forgets_the_chosen_server()
    {
        Settings(new AppSettings
        {
            SubscriptionUrl = "https://old.example/sub",
            PreferredServer = "USA-1",
        });

        SubscriptionBook.MakeActive(new SubscriptionEntry { Url = "https://new.example/sub" });

        var settings = AppSettings.Load(AppSettings.DefaultPath);

        Assert.Equal("https://new.example/sub", settings.SubscriptionUrl);
        Assert.Null(settings.PreferredServer);
    }

    /// <summary>Испорченный файл не мешает открыть раздел.</summary>
    [Fact]
    public void A_broken_file_still_opens_the_section()
    {
        Settings(new AppSettings { SubscriptionUrl = "https://panel.example/sub" });

        Directory.CreateDirectory("config");
        File.WriteAllText(SubscriptionBook.DefaultPath, "{ это не список }");

        Assert.Single(SubscriptionBook.Load().Entries);
    }

    /// <summary>Счёт для показа ссылок не разглашает.</summary>
    /// <remarks>
    /// В том и смысл: ссылка на подписку — это доступ, и показывать её
    /// в сводке на виду незачем.
    /// </remarks>
    [Fact]
    public void The_summary_never_shows_an_url()
    {
        var settings = new AppSettings { SubscriptionUrl = "https://panel.example/sub?token=SECRET" };

        var book = new SubscriptionBook
        {
            Entries = [new SubscriptionEntry { Name = "Основная", Url = settings.SubscriptionUrl }],
        };

        var said = book.Describe(settings);

        Assert.DoesNotContain("SECRET", said, StringComparison.Ordinal);
        Assert.DoesNotContain("http", said, StringComparison.Ordinal);
        Assert.Contains("Основная", said, StringComparison.Ordinal);
    }

    /// <summary>Имя для новой подписки не сталкивается с занятым.</summary>
    [Fact]
    public void A_free_name_is_really_free()
    {
        var book = new SubscriptionBook
        {
            Entries =
            [
                new SubscriptionEntry { Name = "Подписка 1", Url = "https://a" },
                new SubscriptionEntry { Name = "Подписка 2", Url = "https://b" },
            ],
        };

        var name = book.FreeName();

        Assert.DoesNotContain(book.Entries, e => e.Name == name);
    }
}
