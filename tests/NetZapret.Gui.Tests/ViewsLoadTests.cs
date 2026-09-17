using System.Windows.Controls;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Каждый раздел окна создаётся без исключения.
/// </summary>
/// <remarks>
/// <para>
/// Самый дешёвый из тестов на окно и самый доходный. Разметка разбирается
/// не компилятором, а в мгновение создания: опечатка в имени кисти, ссылка
/// на несуществующий стиль, сломанная привязка к ресурсу — всё это проходит
/// сборку молча и роняет вкладку у человека.
/// </para>
/// <para>
/// Перечислены все разделы поимённо, а не собраны обходом сборки. Обход
/// выглядел бы умнее и молчал бы ровно о том, ради чего тест заведён:
/// забытый в списке раздел виден сразу, а необойдённый — никогда.
/// </para>
/// </remarks>
public sealed class ViewsLoadTests
{
    public static TheoryData<string, Func<UserControl>> Views => new()
    {
        { "Состояние", () => new StatusView() },
        { "Маршруты", () => new RoutesView() },
        { "Проверка", () => new CheckView() },
        { "Десинк", () => new DesyncView() },
        { "Файл hosts", () => new HostsView() },
        { "VPN", () => new VpnView() },
        { "DNS", () => new DnsView() },
        { "Журнал", () => new LogView() },
        { "Диагностика", () => new DoctorView() },
        { "Наблюдение", () => new WatchView() },
        { "Ещё", () => new MoreView() },
        { "Первый запуск", () => new OnboardingView() },
    };

    /// <param name="name">
    /// Только ради сообщения об ошибке: без него падение называет лямбду,
    /// и до вкладки приходится доходить перебором.
    /// </param>
    [Theory]
    [MemberData(nameof(Views))]
    public void A_view_is_created_without_throwing(string name, Func<UserControl> make)
    {
        _ = name;

        Sta.Run(() => Assert.NotNull(make()));
    }

    /// <summary>И само окно тоже.</summary>
    /// <remarks>
    /// Отдельно от разделов: окно держит трей, единственный экземпляр
    /// и переключение вкладок, и падало оно самостоятельно — на шрифте
    /// с эмодзи-флагами, когда <c>InvariantGlobalization</c> ещё не был
    /// отменён для этого проекта.
    /// </remarks>
    [Fact]
    public void The_window_itself_is_created_without_throwing()
    {
        Sta.Run(() => Assert.NotNull(new MainWindow()));
    }
}
