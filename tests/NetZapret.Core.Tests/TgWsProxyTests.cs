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

    /// <summary>
    /// «Всегда» (с 0.15.2): прокси держит окно, своим файлом состояния, и надзор
    /// движков его не знает. До 20.10 «Диагностика» при живом прокси звала
    /// перезапускать движки.
    /// </summary>
    [Fact]
    public void DoctorSeesTheProxyTheWindowHolds()
    {
        var settings = new AppSettings { TelegramProxy = true, TelegramProxyAlways = true, TelegramProxySecret = TgWsProxy.NewSecret() };
        var here = typeof(TgWsProxyTests).Assembly.Location;
        var proxy = new ServiceState { Name = TgWsProxyService.ServiceName, Health = ServiceHealth.Healthy };

        // Движки работают без прокси (так и задумано), прокси у окна — живой и здоровый.
        var engines = new SupervisorState { SupervisorProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.Now, Services = [] };
        var window = new SupervisorState { SupervisorProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.Now, Services = [proxy] };

        var held = Assert.Single(Doctor.TelegramProxy(settings, executable: here, state: engines, kept: window));
        Assert.Equal(DoctorLevel.Ok, held.Level);
        Assert.Contains("Работает", held.Text);

        // Окно закрыто — прокси поднимется вместе с ним, а не с движками.
        var gone = window with { SupervisorProcessId = -1 };
        var later = Assert.Single(Doctor.TelegramProxy(settings, executable: here, state: engines, kept: gone));
        Assert.Equal(DoctorLevel.Ok, later.Level);
        Assert.Contains("окном", later.Text);

        // Надзор, поднятый до включения «всегда», держит свой — тоже в счёт.
        var byEngines = engines with { Services = [proxy] };
        Assert.Equal(DoctorLevel.Ok, Assert.Single(Doctor.TelegramProxy(settings, executable: here, state: byEngines, kept: gone)).Level);

        // Без «всегда» прежняя проверка: движки без прокси — оговорка.
        var withEngines = settings with { TelegramProxyAlways = false };
        Assert.Equal(DoctorLevel.Warn, Assert.Single(Doctor.TelegramProxy(withEngines, executable: here, state: engines, kept: window)).Level);
    }

    /// <summary>
    /// Секрет — при первом включении и навсегда: Telegram помнит прокси с ним.
    /// Так включают и вкладка «TG Proxy», и мастер (07.10).
    /// </summary>
    [Fact]
    public void TheSecretIsMadeOnceAndKept()
    {
        var on = TgWsProxy.Switch(new AppSettings(), on: true);

        Assert.True(on.TelegramProxy);
        Assert.True(TgWsProxy.IsSecret(on.TelegramProxySecret));

        var off = TgWsProxy.Switch(on, on: false);
        var again = TgWsProxy.Switch(off, on: true);

        Assert.False(off.TelegramProxy);
        Assert.Equal(on.TelegramProxySecret, off.TelegramProxySecret);
        Assert.Equal(on.TelegramProxySecret, again.TelegramProxySecret);

        // Выключенный ни разу не включённый секрета не заводит.
        Assert.Null(TgWsProxy.Switch(new AppSettings(), on: false).TelegramProxySecret);
    }

    [Fact]
    public void WithoutVpnTheProxyIsWhatMakesTelegramWork()
    {
        Assert.Contains("не подключится", TgWsProxy.Explain(Rules.WorkMode.Desync));
        Assert.Contains("не обязателен", TgWsProxy.Explain(Rules.WorkMode.Route));
        Assert.Contains("не обязателен", TgWsProxy.Explain(Rules.WorkMode.Tunnel));
    }
}
