using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using NetZapret.Core;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Сборка конфига туннеля — из настроек, правил и подписки.
/// </summary>
/// <remarks>
/// <para>
/// Самое дорогое место окна. Вечер 16.09 дал четыре поломки, и все четыре
/// жили здесь: путь со списком, где кириллица ломала разбор аргументов
/// Cygwin; относительный путь к кэшу, роняющий движок на старте; нулевой
/// байт в разделителе, из-за которого каждый рецепт выходил одношаговым;
/// порядок профилей winws2. Ни одну из них не поймал бы ни один
/// из пятисот тестов — они не доставали до <c>gui\</c>.
/// </para>
/// <para>
/// Подписка отдаётся своим сервером на петле, а не подкладывается объектом.
/// Разница существенная: так проверяется и <c>SubscriptionClient</c>, и разбор
/// base64, и заголовки панели — то есть весь путь, а не его середина. Сеть
/// при этом не нужна: слушатель поднимается на 127.0.0.1.
/// </para>
/// </remarks>
public sealed class TunnelConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"netzapret-gui-{Guid.NewGuid():N}");

    private readonly string _was = Directory.GetCurrentDirectory();

    /// <summary>Два сервера: один настоящий, один заведомо негодный.</summary>
    /// <remarks>
    /// Негодный — разделитель подписки: панели вставляют такие строки ради
    /// подписи в списке, а адреса у них нет. Попав в конфиг, они становились
    /// выходом, который не набирается никогда, и группа автоподбора оседала
    /// на нём.
    /// </remarks>
    private const string Subscription = """
        vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@us.example.com:443?security=reality&encryption=none&flow=xtls-rprx-vision&fp=chrome&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=0123456789abcdef&sni=www.microsoft.com&type=tcp#USA
        vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1337?type=tcp#---%20%D0%B4%D0%BE%20%D0%BA%D0%BE%D0%BD%D1%86%D0%B0%20---
        """;

    public TunnelConfigTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "runtime"));

        File.WriteAllText(Path.Combine(_root, "config", "rules.yaml"), """
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
              - match: domain
                value: "*.youtube.com"
                mode: desync
            default:
              mode: direct
            """);

        // Конфиг собирается по относительным путям — так его пишет и окно.
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_was);

        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    /// <summary>Отдаёт подписку с петли, пока идёт проверка.</summary>
    /// <remarks>
    /// Порт берётся у системы: зашитый однажды столкнётся с чужим слушателем,
    /// и тест начнёт падать у одного человека из десяти без всякой причины.
    /// </remarks>
    private sealed class Panel : IDisposable
    {
        private readonly HttpListener _listener = new();

        public string Url { get; }

        public Panel(string body)
        {
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}/sub";

            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var bytes = Encoding.ASCII.GetBytes(
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));

                    context.Response.Headers.Add("profile-title", "Тестовая панель");
                    context.Response.ContentLength64 = bytes.Length;

                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // Слушатель закрыт раньше запроса — обычный конец теста.
                }
            });
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }

        public void Dispose() => _listener.Close();
    }

    private async Task<JsonElement> BuildAsync(Func<AppSettings, AppSettings>? tweak = null)
    {
        using var panel = new Panel(Subscription);

        var settings = new AppSettings
        {
            SubscriptionUrl = panel.Url,
            RulesPath = Path.Combine("config", "rules.yaml"),
            ProxyConfigPath = Path.Combine("runtime", "singbox.json"),
        };

        var outcome = await TunnelConfig.BuildAsync(
            tweak is null ? settings : tweak(settings), CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Message);

        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine("runtime", "singbox.json"))).RootElement;
    }

    /// <summary>Конфиг собирается и оказывается на диске.</summary>
    [Fact]
    public async Task A_config_is_written()
    {
        var root = await BuildAsync();

        Assert.True(root.TryGetProperty("outbounds", out _));
        Assert.True(root.TryGetProperty("inbounds", out _));
        Assert.True(root.TryGetProperty("route", out _));
    }

    /// <summary>
    /// Разделитель подписки в выходы не попадает.
    /// </summary>
    /// <remarks>
    /// Строка без набираемого адреса — <c>0.0.0.0:1337</c> — есть почти
    /// в каждой панели: ею подписывают разделы списка. Попав в группу
    /// автоподбора, она становилась выходом, который не отвечает никогда,
    /// и весь туннель молчал при живых серверах.
    /// </remarks>
    [Fact]
    public async Task A_subscription_separator_never_becomes_an_exit()
    {
        var root = await BuildAsync();

        var tags = root.GetProperty("outbounds")
            .EnumerateArray()
            .Select(o => o.GetProperty("tag").GetString())
            .ToList();

        Assert.Contains(tags, t => t is not null && t.Contains("USA", StringComparison.Ordinal));
        Assert.DoesNotContain(tags, t => t is not null && t.Contains("до конца", StringComparison.Ordinal));
    }

    /// <summary>
    /// Служебный вход поднимается тогда и только тогда, когда его спросят.
    /// </summary>
    /// <remarks>
    /// Разойдясь, эти два решения дают вечно проваливающуюся проверку
    /// у исправного движка: супервизор стучится туда, где никого нет.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_health_inbound_follows_the_setting(bool asked)
    {
        var root = await BuildAsync(s => s with { VerifyTraffic = asked });

        var inbounds = root.GetProperty("inbounds")
            .EnumerateArray()
            .Select(i => i.GetProperty("tag").GetString())
            .ToList();

        Assert.Equal(asked, inbounds.Contains("health-in"));
    }

    /// <summary>WARP добавляется к серверам подписки, а не вместо них.</summary>
    /// <remarks>
    /// Он запасной выход, и подменять им основной — обратное тому, зачем
    /// он заведён.
    /// </remarks>
    [Fact]
    public async Task Warp_is_added_to_the_subscription_not_instead_of_it()
    {
        var root = await BuildAsync(s => s with { WarpEnabled = true });

        var tags = root.GetProperty("outbounds")
            .EnumerateArray()
            .Select(o => o.GetProperty("tag").GetString() ?? string.Empty)
            .ToList();

        Assert.Contains(tags, t => t.Contains("USA", StringComparison.Ordinal));
        Assert.Contains(tags, t => t.Contains("WARP", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Путь к кэшу движка — полный, а не относительный.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Поломка 0.5.4, стоившая вечера: движок запускается со своим текущим
    /// каталогом, и относительный путь уводил его в никуда. Падало на старте,
    /// а выглядело как неисправная подписка.
    /// </para>
    /// <para>
    /// Кэш появляется вместе с WARP и только с ним: хранится там учётная
    /// запись Cloudflare, которую движок заводит себе сам. Поэтому и проверка
    /// здесь, а не в общем случае — в общем случае секции просто нет, и тест
    /// «все пути полные» был бы зелёным, не проверив ничего.
    /// </para>
    /// <para>
    /// Проверяется именно <c>cache_file</c>, а не всякое поле с именем
    /// <c>path</c>: у транспорта это адрес внутри HTTP, вроде <c>/ws</c>,
    /// и требовать от него полного пути значило бы требовать бессмыслицы.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_engine_cache_path_is_absolute()
    {
        var root = await BuildAsync(s => s with { WarpEnabled = true });

        var cache = root.GetProperty("experimental").GetProperty("cache_file");

        Assert.True(cache.GetProperty("enabled").GetBoolean());

        var path = cache.GetProperty("path").GetString();

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(
            Path.IsPathFullyQualified(path!),
            $"путь к кэшу «{path}» не полный — движок разрешит его от своего каталога");
    }

    /// <summary>Без подписки конфиг не собирается, и об этом говорят словами.</summary>
    /// <remarks>
    /// Молчаливый отказ здесь означал бы запуск движков со вчерашним конфигом:
    /// настройка показывается новая, а туннель ведёт себя по-старому.
    /// </remarks>
    [Fact]
    public async Task Without_a_subscription_it_refuses_out_loud()
    {
        var outcome = await TunnelConfig.BuildAsync(
            new AppSettings { SubscriptionUrl = null }, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("одписк", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>Недоступная панель — тоже отказ, а не падение.</summary>
    [Fact]
    public async Task An_unreachable_panel_is_reported_not_thrown()
    {
        var outcome = await TunnelConfig.BuildAsync(
            new AppSettings
            {
                SubscriptionUrl = "http://127.0.0.1:1/sub",
                RulesPath = Path.Combine("config", "rules.yaml"),
            },
            CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("не собрался", outcome.Message, StringComparison.Ordinal);
    }
}
