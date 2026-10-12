using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Имя сервера без хвоста, повторяющего плашку протокола (макет владельца 12.10).
/// </summary>
public sealed class ServerNameTests
{
    [Theory]
    [InlineData("Германия — TLS XHTTP", "TLS XHTTP", "Германия")]
    [InlineData("Россия — Reality XHTTP", "reality xhttp", "Россия")]
    [InlineData("Нидерланды | TLS WS", "TLS WS", "Нидерланды")]
    [InlineData("Польша — XDNS", "KCP", "Польша — XDNS")]
    [InlineData("VLESS TLS XHTTP", "TLS XHTTP", "VLESS TLS XHTTP")]
    [InlineData("TLS XHTTP", "TLS XHTTP", "TLS XHTTP")]
    [InlineData("Финляндия", "", "Финляндия")]
    public void TailRepeatingTheChipIsCut(string name, string chip, string expected)
    {
        Assert.Equal(expected, VpnView.WithoutChip(name, chip));
    }
}
