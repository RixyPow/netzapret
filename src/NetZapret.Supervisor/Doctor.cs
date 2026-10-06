using System.Security.Principal;
using System.Text;
using NetZapret.Core;
using NetZapret.Core.Diagnostics;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Supervisor;

/// <summary>Насколько строка диагностики плоха.</summary>
public enum DoctorLevel
{
    Ok,

    /// <summary>Оговорка: работает, но может подвести.</summary>
    Warn,

    /// <summary>Поломка: с этим не работает.</summary>
    Bad,
}

/// <summary>Одна строка диагностики.</summary>
/// <param name="Open">
/// Что открыть кнопкой рядом со строкой (оснастку Windows); <c>null</c> — кнопки нет.
/// Открывает окно: библиотеке запускать оснастки незачем.
/// </param>
public sealed record DoctorCheck(string Text, DoctorLevel Level, string? ActionLabel = null, string? Open = null);

/// <summary>Раздел диагностики.</summary>
public sealed record DoctorSection(string Title, IReadOnlyList<DoctorCheck> Lines);

/// <summary>
/// Диагностика: всё, от чего зависит работа, разом.
/// </summary>
/// <remarks>
/// <para>
/// Ценность в порядке: проверки идут от того, без чего не работает ничего,
/// к тому, что ломает частности, — и первая же красная строка обычно и есть
/// ответ.
/// </para>
/// <para>
/// До 06.10 проверки жили в окне (раздел «Диагностика»), и в отчёт для разбора
/// не попадали: человек видел жёлтые строки у себя, а в присланном архиве их
/// не было (№18 — безопасный DNS Chrome и служба Flowseal пересказаны словами).
/// Теперь один источник для окна, отчёта и <c>nz doctor</c>.
/// </para>
/// </remarks>
public static class Doctor
{
    /// <param name="elevation">
    /// Проверять ли права. Они описывают процесс, который спрашивает, а не систему:
    /// nz и отчёт из него идут без администратора, и строка «обычные права» в отчёте
    /// звала бы чинить исправную установку.
    /// </param>
    public static IReadOnlyList<DoctorSection> Run(AppSettings settings, bool elevation = true)
    {
        var sections = new List<DoctorSection>();

        if (elevation)
            sections.Add(new("Права", Elevation()));

        sections.Add(new("Папка программы", Place(Path.GetFullPath("."))));
        sections.Add(new("Движки", Engines()));
        sections.Add(new("Десинк", Zapret(settings)));
        sections.Add(new("Правила", Rules(settings)));
        sections.Add(new("Конфиг туннеля", Proxy(settings)));
        sections.Add(new("Другие VPN-клиенты", OtherVpns()));
        sections.Add(new("Имена и hosts", Names(settings)));
        sections.Add(new("Браузеры и сертификаты", Browsers()));
        sections.Add(new("Супервизор", Supervisor()));

        if (settings.TelegramProxy)
            sections.Add(new("Прокси Telegram", TelegramProxy(settings)));

        return sections;
    }

    /// <summary>Итог одной строкой — тот, что окно пишет над списком.</summary>
    public static string Verdict(IReadOnlyList<DoctorSection> sections)
    {
        var lines = sections.SelectMany(s => s.Lines).ToList();
        int bad = lines.Count(l => l.Level == DoctorLevel.Bad);
        int warn = lines.Count(l => l.Level == DoctorLevel.Warn);

        return bad == 0 && warn == 0
            ? $"Всё на месте: проверок {lines.Count}, ни одной жалобы."
            : $"Проверок {lines.Count}: поломок {bad}, оговорок {warn}. "
              + "Красное чинить первым — остальное часто следствие.";
    }

    /// <summary>Текстом — для отчёта и nz.</summary>
    public static string Describe(IReadOnlyList<DoctorSection> sections)
    {
        var text = new StringBuilder();

        text.AppendLine(Verdict(sections));
        text.AppendLine();

        foreach (var section in sections)
        {
            text.AppendLine(section.Title);

            foreach (var line in section.Lines)
            {
                var mark = line.Level switch
                {
                    DoctorLevel.Bad => "ПОЛОМКА",
                    DoctorLevel.Warn => "оговорка",
                    _ => "ок",
                };

                text.AppendLine($"  [{mark}] {line.Text}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static DoctorCheck Ok(string text) => new(text, DoctorLevel.Ok);

    private static DoctorCheck Warn(string text, string? action = null, string? open = null) =>
        new(text, DoctorLevel.Warn, action, open);

    private static DoctorCheck Bad(string text) => new(text, DoctorLevel.Bad);

    private static IReadOnlyList<DoctorCheck> Elevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        return
        [
            elevated
                ? Ok("Администратор: доступны TUN, winws2 и наблюдение.")
                : Bad("Обычные права. Окно должно запускаться от администратора — "
                    + "иначе не поставить драйвер перехвата и не поднять TUN."),
        ];
    }

    /// <summary>
    /// Где лежит программа: облачная синхронизация держит её файлы (CloudFolder).
    /// </summary>
    /// <remarks>
    /// Оговорка, а не поломка: движки в такой папке поднимаются, но обновление
    /// может не заменить занятый файл, а журналы и списки, переписываемые каждую
    /// минуту, гоняются в облако. Папка — текущая: окно при запуске переходит
    /// в корень установки (InstallRoot.MoveTo), nz — тоже.
    /// </remarks>
    public static IReadOnlyList<DoctorCheck> Place(string root, Func<string, string?>? variable = null)
    {
        if (CloudFolder.Of(root, variable) is not { } cloud)
            return [Ok($"Не в облачной папке: {root}.")];

        return
        [
            Warn($"Программа лежит в папке {cloud}: {root}. {cloud} синхронизирует и занимает её файлы — "
                + "обновление может не заменить занятый файл, а журналы и списки, которые движки "
                + "переписывают постоянно, уходят в облако. Перенесите папку NetZapret в обычное место, "
                + @"например C:\NetZapret: закройте программу через трей, перенесите папку целиком "
                + "и запускайте оттуда; автозапуск помнит прежний путь — выключите и включите его "
                + "снова на «Главной». «Рабочий стол» тоже бывает внутри OneDrive — если включено "
                + "его резервное копирование."),
        ];
    }

    /// <summary>Прокси для Telegram Desktop — есть ли он, есть ли секрет, поднят ли.</summary>
    /// <param name="executable">Путь к прокси; <c>null</c> — рядом с программой. Для тестов.</param>
    public static IReadOnlyList<DoctorCheck> TelegramProxy(AppSettings settings, string? executable = null, SupervisorState? state = null)
    {
        var lines = new List<DoctorCheck>();
        var path = executable ?? TgWsProxy.Executable();

        if (!File.Exists(path))
        {
            lines.Add(Bad($"Прокси включён, но его нет в поставке: {path}. Telegram через него не подключится."));
            return lines;
        }

        if (!TgWsProxy.IsSecret(settings.TelegramProxySecret))
        {
            lines.Add(Bad("У прокси нет секрета — выключите и включите его на вкладке «TG Proxy»."));
            return lines;
        }

        state ??= SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
        {
            lines.Add(Ok($"Включён на 127.0.0.1:{settings.TelegramProxyPort}; поднимется вместе с движками."));
            return lines;
        }

        var service = state.Services.FirstOrDefault(s => s.Name == TgWsProxyService.ServiceName);

        lines.Add(service switch
        {
            null => Warn("Прокси включён, но движки запущены без него — перезапустите их."),
            { Health: ServiceHealth.Healthy } => Ok($"Работает на 127.0.0.1:{settings.TelegramProxyPort}."),
            _ => Warn($"127.0.0.1:{settings.TelegramProxyPort}: {EngineHealth.Status(service)}. "
                + "Частая причина — порт занят другим tg-ws-proxy; журнал — runtime\\tg-ws-proxy.log."),
        });

        return lines;
    }

    private static IReadOnlyList<DoctorCheck> Engines()
    {
        var lines = new List<DoctorCheck>();
        var singBox = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

        if (File.Exists(singBox))
        {
            lines.Add(Ok("sing-box на месте."));

            // Отдельный wintun.dll не нужен: sing-box несёт драйвер в себе
            // (sing-tun/internal/wintun, загрузка из памяти — memmod; в exe
            // восемь встроенных образов, проверено 28.09 на 1.14.1-extended).
            // Прежде здесь стояло «нет wintun.dll — TUN не поднимется», и
            // красным это видел каждый, кто ставил из архива: в поставке
            // файла нет и не было (обсуждение #8, человек искал его на GitHub
            // wintun и решил, что из-за него не работает десинк).
        }
        else
        {
            lines.Add(Bad($"sing-box не найден: {singBox}. VPN недоступен, десинк работает."));
        }

        // Проверки на консольную программу рядом здесь больше нет. Она стояла
        // с тех пор, когда окно звало её поднимать движки, и после перехода
        // на собственный супервизор ругалась на исправную установку: в поставке
        // консоли нет вовсе, а «запуск не сработает» — прямая неправда.
        return lines;
    }

    private static IReadOnlyList<DoctorCheck> Zapret(AppSettings settings)
    {
        var paths = ZapretPaths.Discover();

        if (paths is null)
            return [Warn("Установка Zapret не найдена — десинк недоступен.")];

        var lines = new List<DoctorCheck> { Ok($"Установка: {paths.Root}") };

        lines.Add(File.Exists(paths.ExecutablePath)
            ? Ok("winws2.exe на месте.")
            : Bad($"winws2.exe не найден: {paths.ExecutablePath}. Возможно, антивирус увёз "
                + "его в карантин — WinDivert рядом с ним помечается как RiskTool."));

        try
        {
            int count = ZapretPaths.PresetFiles.Count;

            lines.Add(count > 0
                ? Ok($"Пресетов: {count}.")
                : Warn("Пресетов не найдено."));
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Пресеты не читаются: " + ex.GetBaseException().Message));
        }

        // Выбранный пресет мог исчезнуть: настройки переживают обновление,
        // а состав папки — нет. Без этой проверки супервизор поднимется
        // без десинка, сказав об этом только в журнал.
        if (settings.PresetName is { } chosen)
        {
            lines.Add(ZapretPaths.FindPreset(chosen) is not null
                ? Ok($"Выбран пресет: {chosen}.")
                : Bad($"Выбранного пресета «{chosen}» здесь нет — десинк не запустится."));
        }
        else
        {
            lines.Add(Warn("Пресет не выбран — десинк не запустится."));
        }

        // Второй обход рядом — GoodbyeDPI, служба Zapret от Flowseal, чужой
        // winws. Свой winws2 узнаём по номеру процесса из состояния надзора.
        try
        {
            var others = OtherBypassScan.Find(Ours());

            if (others.Count == 0)
            {
                lines.Add(Ok("Другого обхода рядом не видно."));
            }

            foreach (var other in others)
            {
                lines.Add(other.Running
                    ? Bad($"Работает ещё один обход: {other.Name} ({other.Where}). Два перехватчика "
                        + "на одном трафике мешают друг другу — десинк может не работать ни у одного. "
                        + "Закройте его, пока работает NetZapret.")
                    : Warn($"{other.Name} поднимется при старте Windows: {other.Where}. Сейчас не работает, "
                        + "но после перезагрузки встанет рядом с NetZapret. Если он больше не нужен — "
                        + "отключите службу (services.msc) или удалите ту сборку."));
            }
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Не удалось проверить, нет ли другого обхода: " + ex.GetBaseException().Message));
        }

        return lines;
    }

    /// <summary>Номера процессов нашего надзора: свой winws2 и sing-box не чужие.</summary>
    private static HashSet<int> Ours() =>
        (SupervisorState.Load(SupervisorState.DefaultPath)?.Services ?? [])
            .Where(s => s.ProcessId is not null)
            .Select(s => s.ProcessId!.Value)
            .ToHashSet();

    /// <summary>
    /// Чужой VPN рядом: поднятый туннель — поломка, запущенный клиент — оговорка.
    /// </summary>
    /// <remarks>
    /// Владелец 01.10: туннель Zapret KVN рядом — и каждое новое имя разрешалось
    /// по 12 с, трафик шёл то в туннель, то мимо. Различие поломки и оговорки —
    /// в <see cref="OtherVpnScan"/>.
    /// </remarks>
    private static IReadOnlyList<DoctorCheck> OtherVpns()
    {
        var lines = new List<DoctorCheck>();

        try
        {
            var clients = OtherVpnScan.Clients(Ours());
            var tunnels = OtherVpnScan.Tunnels(new SingBoxOptions().TunInterfaceName);
            var names = string.Join(", ", clients.Select(c => c.Name));

            foreach (var tunnel in tunnels)
            {
                lines.Add(Bad($"Поднят чужой туннель: {tunnel.Name} ({tunnel.Description})"
                    + (clients.Count > 0 ? $", рядом запущены: {names}" : string.Empty)
                    + ". Два туннеля спорят за маршруты: трафик идёт то через один, то через другой, "
                    + "имена разрешаются с задержкой, а замеры и проверки врут. "
                    + "Отключитесь в том клиенте, пока работает NetZapret."));
            }

            // Служба вроде happd работает и при закрытом клиенте — оговорку
            // даёт только сам клиент, иначе она висела бы у владельца всегда.
            var open = clients.Where(c => !c.Service).ToList();

            if (tunnels.Count == 0 && open.Count > 0)
            {
                lines.Add(Warn($"Запущен VPN-клиент: {string.Join(", ", open.Select(c => c.Name))}. "
                    + "Своего туннеля он сейчас не держит, но если подключиться в нём в режиме TUN, "
                    + "туннель встанет рядом с нашим и они будут мешать друг другу."));
            }

            if (tunnels.Count == 0 && open.Count == 0)
                lines.Add(Ok("Других VPN-клиентов рядом не видно."));
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Не удалось проверить, нет ли другого VPN: " + ex.GetBaseException().Message));
        }

        return lines;
    }

    private static IReadOnlyList<DoctorCheck> Rules(AppSettings settings)
    {
        var lines = new List<DoctorCheck>();

        try
        {
            var engine = RuleSetLoader.LoadFor(settings);

            var problems = RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

            int own = engine.RuleSet.Rules.Count(r => r.Source == RuleSource.User);

            lines.Add(Ok($"Режим «{settings.DescribeMode()}», правил {engine.RuleSet.Rules.Count}"
                + (own > 0 ? $", из них ваших {own}." : ".")));

            // Ненайденный список не совпадает ни с чем, оставаясь на вид живым:
            // правило есть, а трафик мимо.
            lines.Add(problems.Count == 0
                ? Ok("Все списки на месте.")
                : Bad($"Списков не нашлось: {problems.Count}. Эти правила не действуют — "
                    + string.Join("; ", problems.Take(3))));

            var conflicts = RouteConflicts.Find(engine.RuleSet);

            lines.Add(conflicts.Count == 0
                ? Ok("Правила друг друга не перекрывают.")
                : Warn($"Правил, перекрытых другими: {conflicts.Count}. Побеждает то, "
                    + $"до которого очередь доходит раньше; например {conflicts[0].Loser.Value} "
                    + $"перекрыто {conflicts[0].Winner.Value}."));
        }
        catch (Exception ex)
        {
            lines.Add(Bad("Правила не читаются: " + ex.GetBaseException().Message));
        }

        // Свои маршруты против пинов — то же, что карточка «Маршрутов» и nz routes.
        // 21.09 час разбора «почему инста не грузится» кончился тем, что имя
        // прибито в hosts и потому выведено из-под десинка.
        try
        {
            var clashes = RouteClashes.FromRules(UserRulesFile.Load().Entries, HostsEditor.Pins().Keys.ToList());

            foreach (var clash in clashes)
                lines.Add(Warn($"{clash.Name}: {clash.Outcome}"));
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Свои маршруты с пинами не сверились: " + ex.GetBaseException().Message));
        }

        return lines;
    }

    private static IReadOnlyList<DoctorCheck> Proxy(AppSettings settings)
    {
        var lines = new List<DoctorCheck>();

        if (!settings.NeedsProxy)
        {
            lines.Add(Ok($"Режим «{settings.DescribeMode()}»: туннель не нужен."));
            return lines;
        }

        // Выход — подписка, отдельные ключи или WARP (0.9.0): с одними
        // ключами «подписка не задана» было бы ложной тревогой.
        // С 01.10 WARP — не запасной, а выбор пути: включён — подписки на паузе.
        lines.Add(settings.WarpEnabled
            ? Ok("Туннель идёт через WARP; подписки на паузе.")
            : !string.IsNullOrWhiteSpace(settings.SubscriptionUrl)
                ? Ok("Подписка задана.")
                : settings.KeysEnabled
                    ? Ok("Подписки нет, выход — отдельные ключи.")
                    : Bad("Ни подписки, ни ключей, ни WARP, а режим требует туннеля: серверов нет."));

        var path = settings.ProxyConfigPath;

        lines.Add(File.Exists(path)
            ? Ok($"Конфиг собран: {path}")
            : Warn($"{path} не собран — соберётся при первом запуске движков."));

        lines.Add(settings.PreferredServer is { } server
            ? Ok($"Сервер выбран: {server}.")
            : Ok("Сервер подбирается по задержке."));

        return lines;
    }

    private static IReadOnlyList<DoctorCheck> Names(AppSettings settings)
    {
        var lines = new List<DoctorCheck>
        {
            Ok($"Апстрим туннеля: {settings.DnsServer}"
                + $", путь — {DnsRoutes.Word(settings.DnsVia)}."),
        };

        try
        {
            var found = SystemResolvers.Discover();

            lines.Add(found.Count > 0
                ? Ok($"Резолверы системы: {string.Join(", ", found)}")
                : Warn("Резолверы системы не нашлись."));
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Резолверы системы не читаются: " + ex.GetBaseException().Message));
        }

        try
        {
            if (HostsEditor.WhoReplaced() is { } who)
            {
                lines.Add(Bad($"Файл hosts переписан: {who}. Изменённый hosts он считает "
                    + "признаком заражения и вернул файл к своему умолчанию — вместе "
                    + "со всеми пинами."));
            }
            else
            {
                int pins = HostsEditor.Pins().Count;

                lines.Add(pins == 0
                    ? Ok("Прибитых имён нет.")
                    : Ok($"Прибито имён: {pins}."));
            }
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Файл hosts не читается: " + ex.GetBaseException().Message));
        }

        var guards = SecuritySoftware.Running();

        if (guards.Count > 0)
        {
            lines.Add(Warn($"Работает {string.Join(", ", guards.Select(g => g.Name))}. "
                + "Его сетевой фильтр встаёт на тот же слой, что и наш, и обесценивает "
                + "десинк, ничего об этом не сообщая."));
        }

        return lines;
    }

    private static IReadOnlyList<DoctorCheck> Browsers()
    {
        var lines = new List<DoctorCheck>();

        try
        {
            var bypass = BrowserDns.Scan();

            if (bypass.Count == 0)
            {
                lines.Add(Ok("Браузеры спрашивают имена у системы — пины и маршруты на них действуют."));
            }

            foreach (var b in bypass)
            {
                var provider = string.IsNullOrEmpty(b.Provider) ? "" : $" ({b.Provider})";

                lines.Add(Warn($"{b.Browser} резолвит имена сам, через свой DNS{provider}. "
                    + "Такой браузер обходит и файл hosts, и туннель: пины на него не действуют, "
                    + "а «через VPN» для него не срабатывает. Выключить: " + b.Setting + "."));
            }
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Настройки браузеров не читаются: " + ex.GetBaseException().Message));
        }

        try
        {
            var roots = RussianRoot.Find();

            if (roots.Count == 0)
                lines.Add(Ok("Сертификата НУЦ Минцифры в системе нет."));

            foreach (var root in roots)
            {
                // Окно своего хранилища: сертификаты компьютера — в certlm.msc
                // и только с правами администратора, пользователя — в certmgr.msc.
                // Удаляет человек сам: хранилище доверия — системная настройка
                // безопасности, и у удаления есть цена (сайты банков).
                var console = root.ForAllUsers ? "certlm.msc" : "certmgr.msc";

                lines.Add(Warn($"Установлен сертификат «{root.Subject}» — {root.Place.ToLowerInvariant()}"
                    + (root.ForAllUsers ? ", для всех пользователей" : "") + ". "
                    + "Владелец его ключа может выпустить сертификат на любой сайт, и браузер "
                    + "примет его без предупреждения: это путь к перехвату HTTPS. Цена удаления: "
                    + "сайты банков на сертификатах НУЦ (Сбербанк, ВТБ и другие с августа 2026) "
                    + $"перестанут открываться в Chrome и Edge. Удалить: в окне «{root.Place}» → "
                    + "«Сертификаты» → правой кнопкой по нему → «Удалить».",
                    "Открыть сертификаты",
                    console));
            }
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Хранилище сертификатов не читается: " + ex.GetBaseException().Message));
        }

        return lines;
    }

    /// <summary>
    /// Движки от прошлого запуска, оставшиеся без присмотра.
    /// </summary>
    /// <remarks>
    /// Самая незаметная из неисправностей и потому первая, о которой стоит
    /// сказать. Осиротевший sing-box держит TUN-адаптер, осиротевший winws2 —
    /// WinDivert; новый движок поднимается процессом и не проходит проверку,
    /// а окно показывает «запущен, но не отвечает», не называя причины.
    /// Видно её было только в журнале супервизора.
    /// </remarks>
    private static IReadOnlyList<DoctorCheck> Orphans(SupervisorState? state)
    {
        try
        {
            var ours = state?.Services
                .Select(s => s.ProcessId)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToHashSet() ?? [];

            var strays = new[] { "sing-box", "winws2", "winws" }
                .SelectMany(System.Diagnostics.Process.GetProcessesByName)
                .Where(p => !ours.Contains(p.Id))
                .Select(p => $"{p.ProcessName} (PID {p.Id})")
                .ToList();

            if (strays.Count == 0)
            {
                // Адаптер при живом sing-box — его собственный, а не брошенный.
                // Без этой оговорки строка звала «сиротой» рабочий netzapret0
                // рядом с «sing-box: работает» (владелец, 26.09): чужих движков
                // здесь уже нет, значит живой — наш.
                bool ownTun = System.Diagnostics.Process.GetProcessesByName("sing-box").Length > 0;
                var ghost = ownTun ? null : Ghost();

                return ghost is null
                    ? [Ok("Движков и адаптеров от прошлых запусков нет.")]
                    : [Bad($"В системе остался туннельный адаптер: {ghost}. Живого движка "
                        + "при этом нет — адаптер пережил его. Новый sing-box не сможет "
                        + "создать свой и будет падать с «Cannot create a file when that "
                        + "file already exists». Уберите адаптер в диспетчере устройств "
                        + "(«Сетевые адаптеры» → sing-tun Tunnel → Удалить) либо командой "
                        + "pnputil /remove-device, и запустите движки заново.")];
            }

            return [Bad($"Движки без присмотра: {string.Join(", ", strays)}. Они держат "
                    + "TUN-адаптер и WinDivert, и новый движок не поднимется — он будет "
                    + "числиться запущенным и не отвечать. Остановите движки и запустите "
                    + "заново: запуск из окна убирает такие сам.")];
        }
        catch (Exception ex)
        {
            return [Warn("Процессы не перечисляются: " + ex.GetBaseException().Message)];
        }
    }

    /// <summary>
    /// Туннельный адаптер, переживший свой движок.
    /// </summary>
    /// <remarks>
    /// Ищется по описанию, а не по имени: брошенный адаптер теряет имя
    /// <c>netzapret0</c> и остаётся в системе обычным Ethernet со своим
    /// номером, сохраняя описание «sing-tun Tunnel». Убитый жёстко sing-box
    /// его за собой не убирает — wintun рассчитывает на спокойный выход.
    /// </remarks>
    private static string? Ghost()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(a => a.Description.Contains("sing-tun", StringComparison.OrdinalIgnoreCase))
                .Select(a => $"{a.Name} ({a.Description})")
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // Не смогли посмотреть — молчим: догадка хуже отсутствия строки.
            return null;
        }
    }

    private static IReadOnlyList<DoctorCheck> Supervisor()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool alive = state is not null && state.IsSupervisorAlive();

        var lines = new List<DoctorCheck>(Orphans(alive ? state : null));

        if (!alive)
        {
            lines.Add(Warn("Движки не запущены — обход не работает, трафик идёт напрямую."));
            return lines;
        }

        foreach (var service in state!.Services)
        {
            lines.Add(service.Health switch
            {
                ServiceHealth.Healthy => Ok($"{service.Name}: работает."),

                // Самый коварный случай: процесс жив, а проверка не проходит.
                // Назвать его «работает» значит уверять в исправности молчащей
                // трубы.
                ServiceHealth.Degraded => Warn($"{service.Name}: запущен, но не отвечает."),
                ServiceHealth.Dead => Bad($"{service.Name}: процесс умер."),
                _ => Warn($"{service.Name}: остановлен."),
            });
        }

        return lines;
    }
}
