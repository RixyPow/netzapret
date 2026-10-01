using NetZapret.Subscriptions;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace NetZapret.Core.Tests;

/// <summary>Ключи из буфера, файла и QR-кода (01.10).</summary>
public sealed class KeyImportTests
{
    private const string Conf = """
        [Interface]
        PrivateKey = K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=
        Address = 10.8.1.3/32, fd00::3/128
        MTU = 1376
        Jc = 4
        Jmin = 40
        Jmax = 70
        H1 = 100-200
        I1 = <b 0xc700000001>

        [Peer]
        PublicKey = 0e6a0OqdsnKPvxM3rX/zImqcLAMx0J9xPni/hixPltk=
        PresharedKey = K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=
        Endpoint = [2001:db8::7]:51820
        PersistentKeepalive = 25
        """;

    /// <summary>
    /// Файл живёт ключом-ссылкой, и ссылка обязана разбираться в тот же сервер:
    /// иначе после перезапуска окна ключ из файла оказался бы другим.
    /// </summary>
    [Fact]
    public void A_conf_file_becomes_a_link_that_parses_back_into_the_same_server()
    {
        var found = KeyImport.FromText(Conf, "Мой сервер");

        var link = Assert.Single(found.Keys);
        Assert.StartsWith("wireguard://", link);

        Assert.True(WireGuardConf.TryParse(Conf, "Мой сервер", out var fromFile, out _));
        Assert.True(ProxyUriParser.TryParse(link, out var fromLink, out var error), error);

        Assert.Equal(fromFile!.Credential, fromLink!.Credential);
        Assert.Equal(fromFile.PeerPublicKey, fromLink.PeerPublicKey);
        Assert.Equal(fromFile.PreSharedKey, fromLink.PreSharedKey);
        Assert.Equal("2001:db8::7", fromLink.Host);
        Assert.Equal(fromFile.Port, fromLink.Port);
        Assert.Equal(fromFile.LocalAddresses, fromLink.LocalAddresses);
        Assert.Equal(1376, fromLink.Mtu);
        Assert.Equal(25, fromLink.KeepaliveSeconds);
        Assert.Equal(fromFile.AmneziaOptions, fromLink.AmneziaOptions);
        Assert.Equal("Мой сервер", fromLink.Tag);
    }

    [Fact]
    public void Keys_in_text_are_found()
    {
        var found = KeyImport.FromText("Ваш ключ:\ntrojan://secret@a.example:443#Германия\nудачи");

        Assert.Equal(["trojan://secret@a.example:443#Германия"], found.Keys);
        Assert.Null(found.SubscriptionUrl);
    }

    [Theory]
    [InlineData("https://panel.example/sub/abc", "https://panel.example/sub/abc")]
    [InlineData("happ://add/https://panel.example/sub/abc", "https://panel.example/sub/abc")]
    public void A_subscription_link_is_told_from_keys(string text, string url)
    {
        var found = KeyImport.FromText(text);

        Assert.Empty(found.Keys);
        Assert.Equal(url, found.SubscriptionUrl);
    }

    [Theory]
    [InlineData("просто текст из буфера")]
    [InlineData("ftp://files.example/key")]
    [InlineData("   ")]
    public void Anything_else_says_so(string text)
    {
        var found = KeyImport.FromText(text);

        Assert.Empty(found.Keys);
        Assert.Null(found.SubscriptionUrl);
        Assert.NotNull(found.Problem);
    }

    /// <summary>
    /// Код рисуется здесь же и кладётся с полем вокруг, как на снимке экрана, —
    /// чтение должно найти его не только во весь кадр.
    /// </summary>
    [Fact]
    public void A_qr_code_with_a_key_is_read()
    {
        const string key = "vless://11111111-2222-3333-4444-555555555555@v.example.com:443?security=reality#QR";

        var matrix = new QRCodeWriter().encode(key, BarcodeFormat.QR_CODE, 300, 300);
        const int margin = 150;
        int width = matrix.Width + 2 * margin;
        int height = matrix.Height + 2 * margin;
        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool dark = x >= margin && y >= margin && x < margin + matrix.Width && y < margin + matrix.Height
                    && matrix[x - margin, y - margin];

                int i = (y * width + x) * 4;
                byte value = dark ? (byte)0 : (byte)255;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        Assert.Equal(key, KeyImport.ReadQr(pixels, width, height));
    }

    [Fact]
    public void A_picture_without_a_code_reads_as_nothing()
    {
        var pixels = Enumerable.Repeat((byte)200, 64 * 64 * 4).ToArray();

        Assert.Null(KeyImport.ReadQr(pixels, 64, 64));
    }
}
