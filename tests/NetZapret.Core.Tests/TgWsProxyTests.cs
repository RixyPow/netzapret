using NetZapret.Core;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Прокси для Telegram Desktop — tg-ws-proxy-rs третьим движком (вкладка «TG Proxy», 07.10).
/// </summary>
public sealed class TgWsProxyTests
{
    [Fact]
    public void SecretIsThirtyTwoHexDigitsAndFresh()
    {
        var one = TgWsProxy.NewSecret();

        Assert.True(TgWsProxy.IsSecret(one));
        Assert.Equal(32, one.Length);
        Assert.NotEqual(one, TgWsProxy.NewSecret());

        Assert.False(TgWsProxy.IsSecret(null));
        Assert.False(TgWsProxy.IsSecret("dd" + one));
        Assert.False(TgWsProxy.IsSecret(new string('z', 32)));
    }

    [Fact]
    public void LinkPointsAtThisMachineInPaddedMode()
    {
        var secret = new string('a', 32);

        Assert.Equal($"tg://proxy?server=127.0.0.1&port=1443&secret=dd{secret}", TgWsProxy.Link(1443, secret));
    }

    [Fact]
    public void ArgumentsKeepTheProxyOnThisMachine()
    {
        var arguments = TgWsProxy.Arguments(1443);

        // Без --host прокси слушает все адреса, если видит локальную сеть;
        // без --link-ip в ссылку попадал адрес нашего TUN (замер 07.10).
        Assert.Equal("127.0.0.1", arguments[arguments.ToList().IndexOf("--host") + 1]);
        Assert.Equal("127.0.0.1", arguments[arguments.ToList().IndexOf("--link-ip") + 1]);
        Assert.Equal("1443", arguments[arguments.ToList().IndexOf("--port") + 1]);

        // Медиа (DC203) без доменов за Cloudflare не грузилось — тот же замер.
        Assert.Contains("--default-domains", arguments);
        Assert.DoesNotContain("--secret", arguments);
    }

    [Fact]
    public void ServiceRefusesWithoutExecutableOrSecret()
    {
        var missing = new TgWsProxyService(@"C:\нет\tg-ws-proxy.exe", 1443, TgWsProxy.NewSecret(), ".");
        Assert.Contains("не найден", missing.ValidatePrerequisites());

        var here = typeof(TgWsProxyTests).Assembly.Location;
        var noSecret = new TgWsProxyService(here, 1443, "", ".");
        Assert.Contains("секрета", noSecret.ValidatePrerequisites());

        Assert.Null(new TgWsProxyService(here, 1443, TgWsProxy.NewSecret(), ".").ValidatePrerequisites());
    }

    [Fact]
    public void ReportHidesTheSecret()
    {
        var secret = TgWsProxy.NewSecret();
        var log = $"INFO server:   Secret:        {secret}\nINFO server:     tg://proxy?server=127.0.0.1&port=1443&secret=dd{secret}";

        var redacted = SupportReport.Redact(log, []);

        Assert.DoesNotContain(secret, redacted);
        // Строку «Secret: …» закрывает и общее правило полей-ключей (KeyField).
        Assert.DoesNotContain("Secret:        " + secret[..4], redacted);
        Assert.Contains("tg://proxy?server=127.0.0.1&port=1443&secret=", redacted);
    }

    [Fact]
    public void DoctorNamesWhatIsMissing()
    {
        var settings = new AppSettings { TelegramProxy = true, TelegramProxySecret = TgWsProxy.NewSecret() };

        var noFile = Assert.Single(Doctor.TelegramProxy(settings, executable: @"C:\нет\tg-ws-proxy.exe"));
        Assert.Equal(DoctorLevel.Bad, noFile.Level);

        var here = typeof(TgWsProxyTests).Assembly.Location;

        var noSecret = Assert.Single(Doctor.TelegramProxy(settings with { TelegramProxySecret = null }, executable: here));
        Assert.Equal(DoctorLevel.Bad, noSecret.Level);

        // Движки стоят — прокси поднимется с ними, жаловаться не на что.
        var stopped = new SupervisorState { SupervisorProcessId = -1, StartedAt = DateTimeOffset.Now, Services = [] };
        Assert.Equal(DoctorLevel.Ok, Assert.Single(Doctor.TelegramProxy(settings, executable: here, state: stopped)).Level);
    }
}
