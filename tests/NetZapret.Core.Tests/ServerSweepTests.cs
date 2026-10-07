using NetZapret.Core;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Замер всех серверов — «Замерить все» и замер при запуске (07.10).
/// </summary>
public sealed class ServerSweepTests
{
    /// <summary>
    /// Движок меряет свои выходы не больше чем по два с одного входа: у Trust
    /// все страны на одном адресе, и пачка проверок разом закрывала его
    /// на минуту (28.09), а две разом он держит (владелец 07.10).
    /// </summary>
    [Fact]
    public void TheEngineMeasuresTwoPerEntry()
    {
        Assert.Equal(2, ServerSweep.PerEntry);
    }

    /// <summary>Замер при запуске стоит трафика и запросов к продавцам — по умолчанию выключен.</summary>
    [Fact]
    public void MeasuringOnStartIsOffByDefault()
    {
        Assert.False(new AppSettings().MeasureOnStart);
        Assert.False(AppSettings.Fresh.MeasureOnStart);
    }
}
