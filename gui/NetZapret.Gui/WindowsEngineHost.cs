using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Zapret;

namespace NetZapret.Gui;

/// <summary>
/// Что окно сообщает общей сборке конфига (<see cref="EngineConfigBuilder"/>):
/// реестр Windows, её резолверы, разворот списков Zapret и общий журнал.
/// </summary>
/// <remarks>
/// Сборка с 27.10 живёт в библиотеке, чтобы телефон собирал конфиг тем же
/// кодом. Всё, что знает только Windows и только окно, приходит сюда.
/// </remarks>
internal static class WindowsEngineHost
{
    /// <param name="book">Книга подписок, из которой собирать; <c>null</c> — прочитать с диска.</param>
    public static EngineHost Create(SubscriptionBook? book = null) => new()
    {
        Platform = SingBoxPlatform.Windows,
        Ipv6 = () => !NetZapret.Core.Diagnostics.WindowsIpv6.Disabled(),
        SystemResolvers = SystemResolvers.Discover,
        LoadRules = LoadRules,
        ExpandAddressLists = (entries, root) => AddressListReader.Expand(entries, root, out _),
        Subscriptions = () => SubscriptionSources.Read(book),
        Journal = Journal.Write,
    };

    /// <summary>Правила так, как их увидят движки, — общим кодом с nz.</summary>
    public static (Core.Rules.RuleSet RuleSet, string? ZapretRoot) LoadRules(Core.AppSettings settings)
    {
        var (engine, zapretRoot) = RuleSetExpander.LoadFor(settings);

        return (engine.RuleSet, zapretRoot);
    }
}
