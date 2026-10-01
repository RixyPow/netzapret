using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Отдельные ключи (0.9.0).</summary>
public sealed class KeyRingTests
{
    private const string Trojan = "trojan://secret@a.example:443?sni=a.example#Германия";
    private const string Hy2 = "hysteria2://pass@b.example:4443?sni=b.example#Финляндия";

    [Fact]
    public void Keys_are_told_from_subscription_links()
    {
        Assert.True(KeyRing.IsKey(Trojan));
        Assert.True(KeyRing.IsKey("  VLESS://id@c.example:443#x"));
        Assert.False(KeyRing.IsKey("https://panel.example/sub/abc"));
        Assert.False(KeyRing.IsKey("happ://add/https://panel.example/sub"));
    }

    [Fact]
    public void A_pasted_batch_keeps_only_keys_and_drops_repeats()
    {
        var keys = KeyRing.Split($"Ваши ключи:\n{Trojan}\n\n{Hy2} {Trojan}\nудачи");

        Assert.Equal([Trojan, Hy2], keys);
    }

    [Fact]
    public void Keys_glued_by_a_password_box_are_split_apart()
    {
        // Поле-пароль выбрасывает переносы строк при вставке пачки.
        const string vless = "vless://id@c.example:443?security=reality#Польша";

        var keys = KeyRing.Split(Trojan + vless + Hy2);

        Assert.Equal([Trojan, vless, Hy2], keys);
    }

    [Fact]
    public void Each_server_keeps_its_key()
    {
        var (servers, errors) = KeyRing.Parse([Trojan, "vless://broken", Hy2]);

        Assert.Equal(2, servers.Count);
        Assert.Equal(Trojan, servers[0].Key);
        Assert.Equal("Германия", servers[0].Server.Tag);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Keys_join_the_pool_as_their_own_source()
    {
        var result = await SubscriptionPool.BuildAsync([], CancellationToken.None, Path.GetTempPath(), [Trojan, Hy2]);

        var part = Assert.Single(result.Parts);
        Assert.Equal(KeyRing.Name, part.Source.Name);
        Assert.Equal(["Германия", "Финляндия"], result.Servers.Select(s => s.Tag));
    }
    /// <summary>
    /// Новые с 01.10 — склеенные полем пароля, без переносов. «hysteria2://»
    /// не путается с «hysteria://»: после «hysteria» у него идёт «2», а не «://».
    /// </summary>
    [Fact]
    public void Keys_of_the_new_protocols_are_split_too()
    {
        const string tuic = "tuic://u:p@t.example:443#T";
        const string hy = "hysteria://h.example:443?auth=a#H";
        const string any = "anytls://s@a.example:443#A";
        const string wg = "wg://k@w.example:51820?publickey=x&address=10.0.0.2#W";

        var keys = KeyRing.Split(tuic + Hy2 + hy + any + wg);

        Assert.Equal([tuic, Hy2, hy, any, wg], keys);
    }
}
