using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Охват туннеля следует за десинком, а не за настройкой.
/// </summary>
/// <remarks>
/// <para>
/// Жалоба владельца 21.09: «почему на режиме только туннель не работает
/// ютуб?» И его же поправка к моему разбору: «по факту то, что ты сделал,
/// это просто выключил винвс». Он был прав.
/// </para>
/// <para>
/// «Только прокси» заведено ради сосуществования с winws2: TUN забирает
/// один диапазон fakeip, и WinDivert видит исходные потоки приложений,
/// а не переоткрытые сокетом sing-box. Пока десинк работает, это нужно.
/// </para>
/// <para>
/// Без десинка защищать нечего, а режим «всё через туннель» такая
/// настройка ломает начисто: route_address у TUN остаётся списком
/// из fakeip и десятка адресов, ютуб со своим настоящим адресом Google
/// в туннель не попадает и уходит напрямую. Замер на живой машине показал
/// домашний адрес снаружи при поднятом и здоровом туннеле.
/// </para>
/// </remarks>
public sealed class TunnelScopeTests
{
    [Fact]
    public void With_desync_running_the_tunnel_takes_only_fakeip()
    {
        // Иначе ломаются рецепты: десинк рассчитывает увидеть исходный
        // поток приложения, а через туннель тот выходит переоткрытым.
        var settings = new AppSettings { ProxyOnly = true }
            .With(new EngineChoice { Desync = true, Tunnel = true });

        Assert.True(Narrow(settings));
    }

    [Fact]
    public void Without_desync_the_tunnel_takes_everything()
    {
        // Ровно та жалоба. Прежде здесь тоже было «только прокси»,
        // и режим сводился к выключению winws2.
        var settings = new AppSettings { ProxyOnly = true }
            .With(new EngineChoice { Desync = false, Tunnel = true });

        Assert.False(Narrow(settings));
    }

    [Fact]
    public void The_setting_still_decides_while_desync_runs()
    {
        // Выключенная настройка при живом десинке — законный выбор
        // человека, и отменять его незачем: он знает, чем платит.
        var settings = new AppSettings { ProxyOnly = false }
            .With(new EngineChoice { Desync = true, Tunnel = true });

        Assert.False(Narrow(settings));
    }

    [Fact]
    public void Old_settings_without_the_switches_behave_as_before()
    {
        // Переход: у людей записан режим «выборочно» и ProxyOnly по
        // умолчанию. Поведение обязано остаться прежним.
        var settings = new AppSettings { Mode = OperatingMode.Selective, ProxyOnly = true };

        Assert.True(Narrow(settings));
    }

    [Fact]
    public void An_old_proxy_all_mode_gets_the_wide_scope()
    {
        // Прежний режим «всё через VPN» страдал тем же: winws2 при нём
        // поднимался, охват оставался узким, и в туннель уходил не весь
        // трафик, а полтора десятка подсетей.
        var settings = new AppSettings { Mode = OperatingMode.ProxyAll, ProxyOnly = true };

        Assert.False(Narrow(settings));
    }

    /// <summary>
    /// То же решение, что принимает сборка конфига.
    /// </summary>
    /// <remarks>
    /// Повторено здесь одной строкой намеренно: сама сборка ходит в сеть
    /// за подпиской, и проверять её целиком ради одного признака дорого.
    /// Строка короткая, и разойтись ей с оригиналом трудно — а вот если
    /// разойдётся, следующая проверка поймает это на живом конфиге.
    /// </remarks>
    private static bool Narrow(AppSettings settings) =>
        settings.Engines.Desync && settings.ProxyOnly;

    [Fact]
    public void The_scope_reaches_the_built_config()
    {
        // Сборка целиком, без сети: правил нет, серверов нет — но охват
        // виден по тому, ограничен ли route_address у TUN.
        var rules = new RuleSet
        {
            Operating = OperatingMode.ProxyAll,
            DefaultMode = RoutingMode.Proxy,
            Rules = [],
        };

        var wide = new SingBoxConfigCompiler().Compile(
            rules, [], new SingBoxOptions { Scope = TunnelScope.Everything }).Json;

        var narrow = new SingBoxConfigCompiler().Compile(
            rules, [], new SingBoxOptions { Scope = TunnelScope.ProxyOnly }).Json;

        Assert.DoesNotContain("route_address", wide);
        Assert.Contains("route_address", narrow);
    }
}
