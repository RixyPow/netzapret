using System.Net.Http;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>Один опробованный путь и чем он кончился.</summary>
public sealed record SiteFixStep(string Path, bool Works, string Detail);

/// <summary>Итог «Сайт не открывается».</summary>
/// <param name="Applied">Какой маршрут записан; <c>null</c> — ничего не менялось.</param>
/// <param name="NeedsRestart">Маршрут вступит в силу с перезапуском движков (VPN: подменные адреса раздаёт туннель).</param>
public sealed record SiteFixResult(
    string? Host,
    IReadOnlyList<SiteFixStep> Steps,
    RoutingMode? Applied,
    bool NeedsRestart,
    string Summary);

/// <summary>
/// «Сайт не открывается»: пробует пути по очереди и сам записывает сработавший.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 29.09: людей бесит ставить «напрямую» на каждый сервис руками.
/// Сайты ломает не блокировка, а десинк: списки пресета держат целые сети
/// Google и Cloudflare (часть — под чужими именами: Google /16 в списке
/// Discord, Cloudflare /16 в списке AnyDesk), и рецепт достаётся сайтам,
/// которые у провайдера и не закрыты, — aternos.org, translate.google.com.
/// </para>
/// <para>
/// Путь «напрямую» меряется честно, без перезапуска: имя на время кладётся
/// в щит (<c>runtime\desync-exclude.txt</c>), а winws2 перечитывает списки
/// сам при их изменении (документация bol-van: «перезапуск nfqws2 не нужен»).
/// Путь через VPN — через вход проверки движка: он ведёт в туннель.
/// </para>
/// <para>
/// Порядок — от дешёвого к дорогому: как есть, напрямую, через VPN. Туннель
/// последним — он тратит трафик подписки и прибавляет задержку.
/// </para>
/// </remarks>
public static class SiteFixer
{
    /// <summary>Сколько ждать, пока winws2 перечитает щит после правки.</summary>
    private static readonly TimeSpan ShieldReload = TimeSpan.FromSeconds(2);

    /// <summary>Имя из того, что вставил человек: ссылки, имени, имени с путём.</summary>
    public static string? HostOf(string input)
    {
        var text = input.Trim().Trim('"', '\'');

        if (text.Length == 0)
            return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.IdnHost))
            return null;

        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');

        return host.Contains('.') ? host : null;
    }

    /// <summary>Значение правила: вся зона сайта, без www.</summary>
    public static string RuleValue(string host) =>
        "*." + (host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);

    public static async Task<SiteFixResult> FixAsync(
        string input,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var host = HostOf(input);
        var steps = new List<SiteFixStep>();

        if (host is null)
            return new(null, steps, null, false, "Не понял адрес. Вставьте ссылку или имя сайта, например aternos.org.");

        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
        {
            return new(host, steps, null, false,
                "Движки не запущены — проверять нечего: путь «напрямую» меряется через десинк, "
                + "путь через VPN — через туннель. Запустите движки на «Главной» и повторите.");
        }

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var (engine, _) = NetZapret.Zapret.RuleSetExpander.LoadFor(settings);
        var decision = engine.Evaluate(ConnectionEvent.Describe(host));
        var route = settings.Engines.Effective(decision.Mode, settings.NeedsProxy);

        // 1. Как есть.
        progress?.Report($"Проверяю {host} как есть…");
        var now = await BlockCheck.CheckAsync(host, null, cancellationToken, throughTunnel: route == RoutingMode.Proxy);
        steps.Add(new($"как есть ({Word(route)})", now.Kind == BlockKind.None, now.Describe()));

        if (now.Kind == BlockKind.None)
        {
            return new(host, steps, null, false,
                $"{host} открывается как есть ({Word(route)}) — маршрут менять незачем. Если в браузере "
                + "не открывается, дело не в маршруте: попробуйте обновить страницу без кэша (Ctrl+F5) "
                + "или открыть в другом браузере.");
        }

        // 2. Напрямую — через щит, если сейчас его трогает десинк.
        if (route == RoutingMode.Desync && settings.NeedsDesync)
        {
            progress?.Report($"Пробую {host} напрямую, без десинка…");

            if (await TryShieldedAsync(host, cancellationToken) is { } direct)
            {
                steps.Add(direct.Step);

                if (direct.Step.Works)
                {
                    Save(host, RoutingMode.Direct);

                    return new(host, steps, RoutingMode.Direct, false,
                        $"{host} открывается напрямую, а десинк ему мешал. Записал маршрут «напрямую» "
                        + $"для {RuleValue(host)} — действует уже сейчас, перезапуск не нужен.");
                }
            }
        }

        // 3. Через VPN.
        if (settings.NeedsProxy && route != RoutingMode.Proxy)
        {
            progress?.Report($"Пробую {host} через VPN…");

            var vpn = await ThroughTunnelAsync(host, cancellationToken);
            steps.Add(vpn);

            if (vpn.Works)
            {
                Save(host, RoutingMode.Proxy);

                return new(host, steps, RoutingMode.Proxy, true,
                    $"{host} открывается только через VPN. Записал маршрут «через VPN» для {RuleValue(host)} — "
                    + "заработает после перезапуска движков.");
            }
        }

        var tried = string.Join("; ", steps.Select(s => $"{s.Path} — {s.Detail}"));

        return new(host, steps, null, false,
            $"{host} не открылся ни одним путём ({tried}). Маршрут не менял. "
            + (settings.NeedsProxy ? "Возможно, лежит сам сайт." : "Включите туннель — через VPN он, возможно, откроется."));
    }

    /// <summary>
    /// Меряет имя с выключенным для него десинком: на время кладёт его в щит.
    /// </summary>
    /// <remarks>
    /// Щит восстанавливается дословно, если путь не сработал. Сработал — имя
    /// в щите остаётся: правило «напрямую» будет записано, и при следующем
    /// запуске щит соберётся с ним же.
    /// </remarks>
    private static async Task<(SiteFixStep Step, bool Kept)?> TryShieldedAsync(string host, CancellationToken cancellationToken)
    {
        var path = NetZapret.Zapret.WinwsCommandLine.DefaultExcludeListPath;

        if (!File.Exists(path))
            return null;

        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var bare = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

        bool works = false;
        TargetReport? report = null;

        try
        {
            var patched = original.TrimEnd('\r', '\n') + Environment.NewLine + bare + Environment.NewLine;
            await File.WriteAllTextAsync(path, patched, cancellationToken);

            await Task.Delay(ShieldReload, cancellationToken);

            report = await BlockCheck.CheckAsync(host, null, cancellationToken);
            works = report.Kind == BlockKind.None;
        }
        finally
        {
            if (!works)
                await File.WriteAllTextAsync(path, original, CancellationToken.None);
        }

        return (new SiteFixStep("напрямую", works, report?.Describe() ?? "не проверилось"), works);
    }

    /// <summary>Запрос через вход проверки движка: он ведёт в туннель.</summary>
    /// <remarks>Ответ сайта — путь рабочий: соединение через туннель прошло.</remarks>
    private static async Task<SiteFixStep> ThroughTunnelAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = EngineKeys.Loopback(SingBoxOptions.DefaultHealthPort, EngineKeys.Current()),
                UseProxy = true,
                AllowAutoRedirect = false,
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int code = (int)response.StatusCode;
            // 403 через зарубежный выход — почти всегда проверка на робота
            // Cloudflare, её браузер проходит; 451 — отказ по закону.
            bool works = code < 500 && code != 451;

            return new("через VPN", works, $"ответ {code}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("через VPN", false, "нет ответа за 10 с");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new("через VPN", false, ex.GetBaseException().Message);
        }
    }

    private static void Save(string host, RoutingMode mode)
    {
        var file = UserRulesFile.Load();
        file.Set(MatchKind.Domain, RuleValue(host), mode);
        file.Save();
    }

    private static string Word(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "через VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };
}
