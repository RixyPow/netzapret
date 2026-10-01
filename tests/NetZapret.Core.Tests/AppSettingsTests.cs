using System.Text.Json;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"netzapret-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>
    /// «Файла нет» и «файл не прочитался» — разные ответы (30.09): второй
    /// до того выдавал мастер первого запуска человеку, прошедшему его давно.
    /// </summary>
    [Fact]
    public void MissingAndUnreadableAreTold()
    {
        AppSettings.TryLoad(_path, out var missing);
        Assert.Equal(AppSettings.ReadResult.Missing, missing);

        File.WriteAllText(_path, "{ не json");
        AppSettings.TryLoad(_path, out var broken);
        Assert.Equal(AppSettings.ReadResult.Unreadable, broken);

        // Пустой файл — тот самый обрезанный на полпути записи: тоже не «прочитан».
        File.WriteAllText(_path, string.Empty);
        AppSettings.TryLoad(_path, out var empty);
        Assert.Equal(AppSettings.ReadResult.Unreadable, empty);

        new AppSettings { OnboardingDone = true }.Save(_path);
        var read = AppSettings.TryLoad(_path, out var fine);
        Assert.Equal(AppSettings.ReadResult.Read, fine);
        Assert.True(read.OnboardingDone);
    }

    /// <summary>
    /// Файл, занятый чужим чтением, записывается, когда его отпустят, —
    /// а не падает и не остаётся обрезанным.
    /// </summary>
    /// <remarks>
    /// Читатель отпускает файл из своего потока, а не продолжением задачи.
    /// Продолжение шло через общий пул, и на CI, где тесты идут параллельно
    /// и пул занят, оно запускалось позже, чем Save ждёт (десять попыток по
    /// 50 мс): 01.10 три падения подряд за 483–537 мс — Save сдавался
    /// с «Access to the path is denied», а очистка теста не могла удалить
    /// всё ещё открытый файл. Ошибка была в тесте: сон отдельного потока
    /// система не откладывает, как задачу в занятом пуле.
    /// </remarks>
    [Fact]
    public void SavingWaitsForAReaderAndLeavesNoTemp()
    {
        new AppSettings { PresetName = "до" }.Save(_path);

        var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            reader.Dispose();
        });

        release.Start();

        new AppSettings { PresetName = "после" }.Save(_path);
        release.Join();

        Assert.Equal("после", AppSettings.Load(_path).PresetName);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    /// <summary>
    /// WARP из запасного в выбор пути (01.10) — один раз и по смыслу прежнего выбора.
    /// </summary>
    [Theory]
    // Запасной при подписке — выключается, выбранный сервер остаётся.
    [InlineData("""{ "WarpEnabled": true, "SubscriptionUrl": "https://p/s", "PreferredServer": "NL" }""", false, "NL")]
    // Выбран сервером WARP — он и есть путь, выбор снимается.
    [InlineData("""{ "WarpEnabled": true, "SubscriptionUrl": "https://p/s", "PreferredServer": "Cloudflare WARP" }""", true, null)]
    // Без подписок WARP и был единственным выходом — остаётся.
    [InlineData("""{ "WarpEnabled": true }""", true, null)]
    // Уже переведённые — как записаны: включённый человеком WARP не сбрасывается.
    [InlineData("""{ "WarpIsPath": true, "WarpEnabled": true, "SubscriptionUrl": "https://p/s", "PreferredServer": "NL" }""", true, "NL")]
    public void WarpBecomesAPathOnce(string json, bool warp, string? preferred)
    {
        File.WriteAllText(_path, json);

        var read = AppSettings.Load(_path);

        Assert.Equal(warp, read.WarpEnabled);
        Assert.Equal(preferred, read.PreferredServer);
        Assert.True(read.WarpIsPath);
    }

    /// <summary>Game filter выключается у всех один раз (01.10), а включённый после — остаётся.</summary>
    [Theory]
    [InlineData("""{ "GameFilter": true }""", false)]
    [InlineData("""{ "GameFilterReset": true, "GameFilter": true }""", true)]
    public void GameFilterIsTurnedOffOnce(string json, bool expected)
    {
        File.WriteAllText(_path, json);

        var read = AppSettings.Load(_path);

        Assert.Equal(expected, read.GameFilter);
        Assert.True(read.GameFilterReset);
    }

    /// <summary>
    /// Настройки с нуля, сохранённые с включённым game filter, не переводятся ещё раз.
    /// </summary>
    [Fact]
    public void FreshSettingsKeepAGameFilterTurnedOnLater()
    {
        (AppSettings.Fresh with { GameFilter = true }).Save(_path);

        Assert.True(AppSettings.Load(_path).GameFilter);
    }

    [Fact]
    public void SettingsSurviveRoundTrip()
    {
        var settings = new AppSettings
        {
            SubscriptionUrl = "https://example.com/sub/token",
            Mode = OperatingMode.ProxyAll,
            PresetName = "Universal V6",
            PreferredServer = "🇷🇺 Hysteria2 | Россия",
            VerifyTraffic = true,
        };

        settings.Save(_path);
        var loaded = AppSettings.Load(_path);

        Assert.Equal(OperatingMode.ProxyAll, loaded.Mode);
        Assert.Equal("Universal V6", loaded.PresetName);
        Assert.Equal("🇷🇺 Hysteria2 | Россия", loaded.PreferredServer);
        Assert.True(loaded.VerifyTraffic);
    }

    /// <summary>
    /// Тема хранится строкой, и неизвестное значение читается как тёмная.
    /// </summary>
    /// <remarks>
    /// Настройки старше выбора тем поля вовсе не содержат, и падать на этом
    /// нельзя: тёмная была единственной, она же и остаётся умолчанием.
    /// </remarks>
    /// <summary>
    /// Выключатели пишутся один раз — своими полями. Вычисляемый блок
    /// «Engines» ложился рядом вторым местом тех же настроек, спорящим
    /// с первым; при чтении он и так выводится заново.
    /// </summary>
    [Fact]
    public void EnginesAreNotWrittenButSurviveRoundTrip()
    {
        new AppSettings().With(new Rules.EngineChoice { Desync = false, Tunnel = true }).Save(_path);

        Assert.DoesNotContain("\"Engines\"", File.ReadAllText(_path));

        var read = AppSettings.Load(_path).Engines;

        Assert.False(read.Desync);
        Assert.True(read.Tunnel);
    }

    /// <summary>
    /// Вид меню трея: у старых настроек, где полей ещё нет, — размытие
    /// и плотность 60 %, то, что владелец видел 24.09. Выбор человека
    /// переживает сохранение.
    /// </summary>
    [Fact]
    public void TrayLookDefaultsToBlurAndSurvivesRoundTrip()
    {
        File.WriteAllText(_path, "{ \"DnsServer\": \"8.8.8.8\" }");

        var old = AppSettings.Load(_path);

        Assert.True(old.TrayBlur);
        Assert.Equal(60, old.TrayDensity);

        (old with { TrayBlur = false, TrayDensity = 90 }).Save(_path);
        var read = AppSettings.Load(_path);

        Assert.False(read.TrayBlur);
        Assert.Equal(90, read.TrayDensity);
    }

    /// <summary>
    /// Прежнее умолчание 8.8.8.8 переводится на 8.8.4.4 (27.09): у многих
    /// операторов первый закрыт по DoH. Прочий выбор человека не трогается.
    /// </summary>
    [Fact]
    public void OldGoogleDefaultMovesToOpenAddress()
    {
        File.WriteAllText(_path, "{ \"DnsServer\": \"8.8.8.8\" }");
        Assert.Equal("8.8.4.4", AppSettings.Load(_path).DnsServer);

        File.WriteAllText(_path, "{ \"DnsServer\": \"1.1.1.1\" }");
        Assert.Equal("1.1.1.1", AppSettings.Load(_path).DnsServer);

        Assert.Equal("8.8.4.4", new AppSettings().DnsServer);
    }

    [Fact]
    public void ThemeSurvivesAndDefaultsToDark()
    {
        new AppSettings { Theme = "light" }.Save(_path);

        Assert.Equal("light", AppSettings.Load(_path).Theme);

        // Файл без поля — ровно то, что лежит у всех, кто ставил до 0.5.5.
        File.WriteAllText(_path, """{ "Mode": "Selective" }""");

        Assert.Null(AppSettings.Load(_path).Theme);
    }

    [Fact]
    public void ModeIsStoredAsNameNotNumber()
    {
        // Число в файле настроек поехало бы при любой вставке значения
        // в середину перечисления, причём молча.
        new AppSettings { Mode = OperatingMode.ProxyAll }.Save(_path);

        Assert.Contains("ProxyAll", File.ReadAllText(_path));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = AppSettings.Load(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json"));

        Assert.Equal(OperatingMode.Selective, settings.Mode);
        Assert.True(settings.ProxyOnly);

        // Пресет по умолчанию задан, а не пуст: с пустым первый запуск молча
        // остаётся без десинка, и человек узнаёт об этом по неоткрывающимся
        // сайтам, а не из настроек.
        Assert.Equal(AppSettings.DefaultPresetName, settings.PresetName);
    }

    [Fact]
    public void CorruptedFileGivesDefaultsInsteadOfThrowing()
    {
        // Иначе испорченный файл настроек не даст программе запуститься вовсе.
        File.WriteAllText(_path, "{ это не json");

        Assert.Equal(OperatingMode.Selective, AppSettings.Load(_path).Mode);
    }

    [Fact]
    public void NullPresetMeansDesyncIsNotStarted()
    {
        var withoutPreset = new AppSettings { PresetName = null };

        Assert.Equal("не запускать", withoutPreset.DescribePreset());
        Assert.False(withoutPreset.NeedsDesync);
    }

    [Fact]
    public void OutOfTheBoxThereIsAPresetAndNoChosenServer()
    {
        var settings = new AppSettings();

        Assert.Equal(AppSettings.DefaultPresetName, settings.DescribePreset());
        Assert.True(settings.NeedsDesync);
        Assert.Equal("авто (по задержке)", settings.DescribeServer());
    }
}

public class PreferredServerTests
{
    private static ProxyServer Server(string tag) => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = tag,
        Host = "example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Security = "tls",
    };

    private static JsonElement Selector(string? preferred)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var json = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server("Россия"), Server("США")], new SingBoxOptions
            {
                PreferredServerTag = preferred,
            }).Json;

        return JsonDocument.Parse(json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "selector")
            .Clone();
    }

    [Fact]
    public void PinnedServerBecomesTheSelectorDefault()
    {
        Assert.Equal("США", Selector("США").GetProperty("default").GetString());
    }

    [Fact]
    public void WithoutPinTheLatencyTestIsUsed()
    {
        Assert.Equal("auto-latency", Selector(null).GetProperty("default").GetString());
    }

    [Fact]
    public void StaleServerNameFallsBackToLatencyTest()
    {
        // Тег мог остаться в настройках от прежней подписки. Ссылка
        // на отсутствующий outbound не дала бы конфигу запуститься вовсе.
        Assert.Equal("auto-latency", Selector("Сервер которого больше нет").GetProperty("default").GetString());
    }
}
