using System.Net;
using System.Text;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Описание сервера от панели и разбор подписки для отчёта — отчёт DxHell 27.10.
/// </summary>
/// <remarks>
/// Ключи и адреса выдуманы. Описания — те, что пришли в его подписке:
/// «Для LTE (белые списки)» и «Для WiFi (не тратит ГБ)».
/// </remarks>
public sealed class ServerDescriptionTests
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Tail_is_cut_from_the_name_and_decoded()
    {
        var (name, description) = ServerDescription.Split("🇷🇺 Обход 💫?serverDescription=" + Base64("Для LTE (белые списки)"));

        Assert.Equal("🇷🇺 Обход 💫", name);
        Assert.Equal("Для LTE (белые списки)", description);
    }

    [Fact]
    public void Name_without_tail_stays_as_is()
    {
        Assert.Equal(("Германия", (string?)null), ServerDescription.Split("Германия"));

        // Из одного хвоста имени не выйдет — оставить, как пришло.
        var bare = "?serverDescription=" + Base64("x");
        Assert.Equal(bare, ServerDescription.Split(bare).Name);
    }

    [Fact]
    public void Parsed_subscription_keeps_the_description_apart()
    {
        var remark = Uri.EscapeDataString("🇩🇪 Германия 🔥?serverDescription=" + Base64("Для WiFi (не тратит ГБ)"));
        var body = $"vless://{Uuid}@203.0.113.7:443?security=tls&type=tcp&sni=example.com#{remark}";

        var server = Assert.Single(SubscriptionParser.ParseBody(body).Servers);

        Assert.Equal("🇩🇪 Германия 🔥", server.Tag);
        Assert.Equal("Для WiFi (не тратит ГБ)", server.Description);
    }

    [Fact]
    public void Saved_tags_lose_the_tail_on_read()
    {
        var tail = "?serverDescription=" + Base64("Для LTE (белые списки)");
        var path = Path.Combine(Path.GetTempPath(), $"nz-settings-{Guid.NewGuid():N}.json");

        try
        {
            new AppSettings
            {
                PreferredServer = "Обход 💫" + tail,
                AutoPickExcluded = ["Обход №3" + tail, "Латвия"],
                Pools = [new ServerPool("ru", "Россия", ["Обход №1" + tail], Preferred: "Обход №1" + tail)],
            }.Save(path);

            var read = AppSettings.Load(path);

            Assert.Equal("Обход 💫", read.PreferredServer);
            Assert.Equal(["Обход №3", "Латвия"], read.AutoPickExcluded);
            Assert.Equal(["Обход №1"], read.Pools[0].Servers);
            Assert.Equal("Обход №1", read.Pools[0].Preferred);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Digest_names_why_a_server_is_dropped_and_shows_no_keys()
    {
        var body = string.Join('\n',
            $"vless://{Uuid}@203.0.113.7:8443?security=reality&type=xhttp&pbk=&sni=example.com#Пустой%20ключ",
            $"vless://{Uuid}@0.0.0.0:1#━━ ОБХОД ━━",
            $"vless://{Uuid}@203.0.113.8:443?security=tls&type=tcp&sni=example.com#Живой");

        var lines = SubscriptionParser.ParseBody(body).Servers.Select(SubscriptionDigest.Line).ToList();

        Assert.Contains("отброшен: у REALITY нет годного открытого ключа", lines[0]);
        Assert.Contains("xhttp", lines[0]);
        Assert.Contains("отброшен: адреса нет", lines[1]);
        Assert.DoesNotContain("отброшен", lines[2]);
        Assert.Contains("203.0.113.8:443", lines[2]);
        Assert.All(lines, line => Assert.DoesNotContain(Uuid, line));
    }

    [Theory]
    [InlineData("198.18.0.5", true)]
    [InlineData("198.19.255.1", true)]
    [InlineData("198.20.0.1", false)]
    [InlineData("84.32.209.146", false)]
    [InlineData("fc00::7", true)]
    [InlineData("fc00:4000::1", false)]
    public void Fake_addresses_of_the_engine_are_recognised(string address, bool fake)
    {
        Assert.Equal(fake, PanelBypass.IsFake(IPAddress.Parse(address)));
    }
}
