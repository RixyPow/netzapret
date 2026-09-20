using NetZapret.Core;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Что поднимается, когда заворачивать трафик некуда.
/// </summary>
/// <remarks>
/// <para>
/// Прежде не поднималось ничего. Режим решался одним лишь <c>Mode</c>,
/// выборочный требовал туннеля, сборка конфига отвечала «Подписка не задана»,
/// запуск отказывался — и десинк, которому подписка не нужна вовсе,
/// не получал своего шанса. Программа без подписки выглядела нерабочей
/// целиком, хотя половина её работы от подписки не зависит.
/// </para>
/// <para>
/// Понижать режим молча при этом нельзя. «Всё через VPN» без выхода
/// превратилось бы в «ничего через VPN», то есть в обратное обещанному,
/// и трафик, который человек просил спрятать, пошёл бы открыто.
/// </para>
/// </remarks>
public sealed class NoSubscriptionTests
{
    /// <summary>
    /// Выборочный без подписки — это десинк, и туннель не поднимается.
    /// </summary>
    /// <remarks>
    /// В этом режиме всё и так идёт мимо туннеля, кроме выведенных в него
    /// правилами. Выводить некуда — значит поднимать TUN не за чем.
    /// </remarks>
    [Fact]
    public void Selective_without_a_subscription_needs_no_tunnel()
    {
        var settings = new AppSettings { Mode = OperatingMode.Selective, SubscriptionUrl = null };

        Assert.False(settings.NeedsProxy);
        Assert.True(settings.NeedsDesync);
    }

    /// <summary>Пустая и пробельная ссылка — то же самое, что её отсутствие.</summary>
    /// <remarks>
    /// Строка из пробелов попадает в файл настроек от правки руками,
    /// и считать её заданной подпиской значило бы упереться в туннель,
    /// которого нет.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_url_counts_as_no_subscription(string url)
    {
        Assert.False(new AppSettings { Mode = OperatingMode.Selective, SubscriptionUrl = url }.NeedsProxy);
    }

    /// <summary>С подпиской выборочный по-прежнему поднимает туннель.</summary>
    [Fact]
    public void Selective_with_a_subscription_still_needs_the_tunnel()
    {
        var settings = new AppSettings
        {
            Mode = OperatingMode.Selective,
            SubscriptionUrl = "https://panel.example/sub",
        };

        Assert.True(settings.NeedsProxy);
    }

    /// <summary>
    /// Включённый WARP — тоже выход, и один он туннель оправдывает.
    /// </summary>
    /// <remarks>
    /// WARP затем и заведён, чтобы работать, когда подписки нет. Прежде
    /// сборка конфига отказывалась при пустой ссылке, не глядя
    /// на выключатель, и включённый WARP в одиночку не поднимался вовсе.
    /// </remarks>
    [Fact]
    public void Warp_alone_is_a_tunnel_exit()
    {
        var settings = new AppSettings
        {
            Mode = OperatingMode.Selective,
            SubscriptionUrl = null,
            WarpEnabled = true,
        };

        Assert.True(settings.HasTunnelExit);
        Assert.True(settings.NeedsProxy);
    }

    /// <summary>
    /// «Всё через VPN» без выхода не понижается до десинка.
    /// </summary>
    /// <remarks>
    /// Самая важная проверка файла. Молчаливое понижение здесь означало бы,
    /// что трафик, который человек просил спрятать, пошёл открыто — и узнал
    /// бы он об этом не от нас. Пусть лучше сборка конфига откажется вслух.
    /// </remarks>
    [Theory]
    [InlineData(OperatingMode.ProxyAll)]
    [InlineData(OperatingMode.ProxyStrict)]
    public void All_through_vpn_never_silently_becomes_desync(OperatingMode mode)
    {
        var settings = new AppSettings { Mode = mode, SubscriptionUrl = null };

        Assert.True(settings.NeedsProxy);
    }

    /// <summary>Выключенное остаётся выключенным, подписка там ни при чём.</summary>
    [Fact]
    public void Off_stays_off()
    {
        var settings = new AppSettings
        {
            Mode = OperatingMode.Off,
            SubscriptionUrl = "https://panel.example/sub",
        };

        Assert.False(settings.NeedsProxy);
        Assert.False(settings.NeedsDesync);
    }

    /// <summary>«Только десинк» туннеля не просит и с подпиской.</summary>
    [Fact]
    public void Desync_only_ignores_the_subscription()
    {
        var settings = new AppSettings
        {
            Mode = OperatingMode.DesyncOnly,
            SubscriptionUrl = "https://panel.example/sub",
        };

        Assert.False(settings.NeedsProxy);
        Assert.True(settings.NeedsDesync);
    }

    /// <summary>
    /// Человеку говорят, что вышло на деле, а не что он выбрал.
    /// </summary>
    /// <remarks>
    /// Иначе обман в одну строку: читаешь «выборочно», ждёшь, что часть имён
    /// пойдёт через VPN, а туннеля нет вовсе — и не понимаешь, почему
    /// правило «через VPN» ничего не меняет.
    /// </remarks>
    [Fact]
    public void The_mode_is_described_by_what_actually_runs()
    {
        var without = new AppSettings { Mode = OperatingMode.Selective, SubscriptionUrl = null };
        var with = new AppSettings
        {
            Mode = OperatingMode.Selective,
            SubscriptionUrl = "https://panel.example/sub",
        };

        Assert.Contains("только десинк", without.DescribeMode());
        Assert.Equal("выборочно", with.DescribeMode());
    }

    /// <summary>
    /// Без туннеля супервизор получает <c>--no-proxy</c>, а десинк — пресет.
    /// </summary>
    /// <remarks>
    /// Сквозная проверка: именно эта строка решает, что поднимется на деле.
    /// Разойдись она с <see cref="AppSettings.NeedsProxy"/> — окно показывало бы
    /// одно, а движки делали другое.
    /// </remarks>
    [Fact]
    public void Without_a_subscription_the_engines_get_desync_only()
    {
        var settings = new AppSettings
        {
            Mode = OperatingMode.Selective,
            SubscriptionUrl = null,
            PresetName = "Universal V8",
        };

        Assert.False(settings.NeedsProxy);
        Assert.True(settings.NeedsDesync);
        Assert.Equal("Universal V8", settings.DescribePreset());
    }
}
