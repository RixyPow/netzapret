using NetZapret.Core.Diagnostics;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Чей обход запускает служба — по её командной строке.</summary>
public sealed class OtherBypassTests
{
    [Theory]
    [InlineData(@"""C:\zapret-discord-youtube\bin\winws.exe"" --wf-tcp=80,443 --filter-udp=443", "Zapret (winws)")]
    [InlineData(@"C:\Zapret\winws2.exe --lua-init=@lua\zapret-lib.lua", "Zapret 2 (winws2)")]
    [InlineData(@"""C:\Program Files\GoodbyeDPI\x86_64\goodbyedpi.exe"" -5 --blacklist russia.txt", "GoodbyeDPI")]
    public void Bypass_services_are_recognised(string image, string expected)
    {
        Assert.Equal(expected, OtherBypassScan.Classify(image));
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p")]
    [InlineData(@"\SystemRoot\System32\drivers\WinDivert64.sys")]
    [InlineData(@"""C:\Program Files\winwsreport\agent.exe""")]
    public void Other_services_are_not(string image)
    {
        Assert.Null(OtherBypassScan.Classify(image));
    }
}
