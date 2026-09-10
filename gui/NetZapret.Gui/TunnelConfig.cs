using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Zapret;

namespace NetZapret.Gui;

/// <summary>Чем закончилась сборка конфига.</summary>
internal sealed record BuildOutcome(bool Ok, string Message);

/// <summary>
/// Собирает конфиг туннеля из настроек, правил и подписки.
/// </summary>
/// <remarks>
/// <para>
/// Без этого окно было несамостоятельным не в смысле удобства, а буквально:
/// оно запускало движки с тем <c>runtime\singbox.json</c>, который лежал на
/// диске, и смена сервера, правки маршрутов и переключение режима доходили
/// до туннеля только после того, как конфиг соберёт консоль. Настройка при
/// этом показывалась новая — расходилось только поведение.
/// </para>
/// <para>
/// Повторяет то, что консольное меню делает перед каждым запуском. Взять
/// оттуда нельзя: и меню, и сборка конфига — <c>internal</c> в
/// <c>NetZapret.Cli</c>, а библиотеки, из которых всё это складывается,
/// у окна те же самые.
/// </para>
/// </remarks>
internal static class TunnelConfig
{
    public static async Task<BuildOutcome> BuildAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            return new BuildOutcome(false, "Подписка не задана — добавьте ссылку в разделе «VPN».");

        try
        {
            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            // Режим из настроек перекрывает файл правил: окно не должно
            // переписывать базовый YAML, который ведётся руками.
            var ruleSet = engine.RuleSet with { Operating = settings.Mode };

            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), cancellationToken);

            var zapretRoot = ZapretPaths.Discover()?.Root;
            var capture = AddressListReader.Expand(ruleSet.CaptureEntries, zapretRoot, out _);

            RuleSetExpander.Expand(ruleSet, zapretRoot);

            // hosts бьёт любой резолв, включая наш: прибитый там домен
            // не получит fakeip и уйдёт мимо туннеля, сколько бы правил
            // на него ни стояло.
            var pinned = HostsFile.CollectPinnedProxyAddresses(ruleSet, out _);

            // Прибитые имена, идущие мимо туннеля, выводятся из-под десинка.
            // Пин — выбранный руками адрес, а десинк судит по имени и про
            // подмену не знает: он применяет к постороннему узлу рецепт,
            // выверенный на настоящей сети доставки, и рвёт рукопожатие.
            WinwsCommandLine.WriteExcludeList(HostsFile.CollectPinnedDesyncExclusions(ruleSet));

            var addresses = AddressOverrides.Merge(new Dictionary<string, string>(), AddressOverrides.Load());

            var result = new SingBoxConfigCompiler().Compile(ruleSet, info.Servers, new SingBoxOptions
            {
                Scope = settings.ProxyOnly ? TunnelScope.ProxyOnly : TunnelScope.Everything,
                DnsServerAddresses = settings.ProxyOnly ? SystemResolvers.Discover() : Array.Empty<string>(),
                DnsServer = settings.DnsServer,
                DnsThroughTunnel = settings.DnsThroughTunnel,
                PreferredServerTag = settings.PreferredServer,
                ForeignExitsOnly = settings.ForeignExitsOnly,

                // Вход проверки поднимается ровно тогда, когда супервизор
                // будет через него стучаться. Разойдясь, эти два решения дают
                // вечно проваливающуюся проверку у исправного движка.
                HealthInbound = settings.VerifyTraffic,

                CaptureAddresses = capture,
                PinnedProxyAddresses = pinned,
                AddressOverrides = addresses,
            });

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);

            var note = $"Конфиг собран: {result.UsedServers.Count} серверов";

            if (result.SkippedServers.Count > 0)
                note += $", {result.SkippedServers.Count} пропущено";

            return new BuildOutcome(true, note + ".");
        }
        catch (Exception ex)
        {
            return new BuildOutcome(false, "Конфиг не собрался: " + ex.GetBaseException().Message);
        }
    }
}
