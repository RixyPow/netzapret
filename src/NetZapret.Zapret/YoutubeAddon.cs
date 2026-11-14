using NetZapret.Core;

namespace NetZapret.Zapret;

/// <summary>
/// Рецепт узлов видео YouTube и выброс QUIC к ним — поверх любого пресета, по итогу
/// автонастройки (<see cref="YoutubeTune"/>).
/// </summary>
/// <remarks>
/// <para>
/// Делает с пресетом то же, что пресет «Universal V13 YT FIX» несёт в файле: модуль
/// <c>nz-video.lua</c>, шаг <c>nz_first2</c> по имени перед рецептом в секциях googlevideo
/// и youtube, секции по адресам узлов видео для SYN — над IP fallback, иначе её подделка
/// SYN заберёт 209.85 и 172.217 себе. Пресет, в котором шаг уже есть (сам YT FIX),
/// не трогается: он решил за себя.
/// </para>
/// <para>
/// Преобразуется пресет, а не готовая строка: тогда и свои рецепты «Маршрутов»
/// (<see cref="PresetCompanions"/>) видят шаг секции, которую подменяют.
/// </para>
/// </remarks>
public static class YoutubeAddon
{
    /// <summary>Модуль, который несёт шаг <c>nz_first2</c>.</summary>
    public const string LuaInit = "--lua-init=@lua/nz-video.lua";

    /// <summary>Сети узлов видео, где множитель окна можно занизить: их подтверждения в перехвате.</summary>
    public static IReadOnlyList<(string From, string To)> MainNets { get; } =
        [("74.125.0.0", "74.125.255.255"), ("173.194.0.0", "173.194.255.255")];

    private const string MainCidr = "74.125.0.0/16,173.194.0.0/16";

    /// <summary>Запасные сети узлов — среди фронтов Google: здесь окно без множителя, 64 КБ.</summary>
    private const string SpareCidr = "209.85.128.0/17,172.217.0.0/16";

    /// <summary>Применяет настройку; без неё или без нужды — тот же пресет.</summary>
    public static ZapretPreset Apply(ZapretPreset preset, YoutubeTune? tune)
    {
        if (tune is not { Active: true })
            return preset;

        var globals = preset.GlobalArguments.ToList();
        var sections = preset.Sections.ToList();
        bool hasRecipe = globals.Concat(sections.SelectMany(s => s.RawArguments))
            .Any(a => a.StartsWith("--lua-desync=nz_first2", StringComparison.Ordinal));

        if (tune.Nodes && !hasRecipe)
        {
            bool doubled = tune.Doubled && Capture(globals);

            if (!globals.Contains(LuaInit))
                globals.Add(LuaInit);

            for (int i = 0; i < sections.Count; i++)
            {
                if (IsVideoNameSection(sections[i]))
                    sections[i] = WithNameStep(sections[i], $"nz_first2:reset:far={tune.FarMs}");
            }

            // Над IP fallback, как в YT FIX: SYN к узлам должна взять секция по адресам узлов.
            int at = sections.FindIndex(s => s.Name.Contains("IP fallback", StringComparison.OrdinalIgnoreCase));

            if (at < 0)
                at = sections.Count;

            sections.InsertRange(at,
            [
                SynSection("YouTube: узлы видео, малое окно по надобности", MainCidr,
                    doubled ? $"nz_first2:syn:far={tune.FarMs}:scale=1" : $"nz_first2:syn:far={tune.FarMs}", doubled),
                SynSection("YouTube: узлы видео, малое окно по надобности, запасные сети", SpareCidr,
                    $"nz_first2:syn:far={tune.FarMs}", all: false),
            ]);
        }

        if (tune.DropQuic)
            sections.Insert(0, DropQuicSection());

        return preset with { GlobalArguments = globals, Sections = sections };
    }

    /// <summary>
    /// Даёт ли смена настройки другую строку winws2. Нет — перезапуск движков и перезамер
    /// ни к чему: 14.11 «Записать» без изменений перезапустило движки, перемерило, и разброс
    /// (27 → 21 из 30) читался как «стало хуже».
    /// </summary>
    /// <param name="presetHasRecipe">
    /// У пресета свой рецепт (YT FIX): рецепт, окно и порог надстройки не применяются, и
    /// различаться может только выброс QUIC. Без этого 14.11 «рецепт, окно вдвое» → «ничего»
    /// на YT FIX сочлось сменой строки, и окно снова перезапустило движки.
    /// </param>
    public static bool SameLine(YoutubeTune? a, YoutubeTune? b, bool presetHasRecipe = false)
    {
        (bool, bool, int, bool) Key(YoutubeTune? t) => t is { Active: true }
            ? presetHasRecipe
                ? (false, false, 0, t.DropQuic)
                : (t.Nodes, t.Nodes && t.Doubled, t.Nodes ? t.FarMs : 0, t.DropQuic)
            : (false, false, 0, false);

        return Key(a) == Key(b);
    }

    /// <summary>Что включено — словами, для журнала и карточки.</summary>
    public static string Describe(YoutubeTune tune)
    {
        var parts = new List<string>();

        if (tune.Nodes)
            parts.Add($"рецепт узлов видео{(tune.Doubled ? ", окно вдвое шире" : string.Empty)}, далёкие — от {tune.FarMs} мс");

        if (tune.DropQuic)
            parts.Add("QUIC к узлам видео выброшен");

        return parts.Count == 0 ? "ничего не меняется" : string.Join("; ", parts);
    }

    /// <summary>
    /// Пустые подтверждения к сетям узлов — в перехват (<c>--wf-tcp-empty=1</c> и условие в
    /// сыром фильтре); <c>false</c> — у пресета фильтр файлом или полный, слить не с чем.
    /// </summary>
    private static bool Capture(List<string> globals)
    {
        const string raw = "--wf-raw-filter=";

        if (globals.Any(a => a.StartsWith("--wf-raw=", StringComparison.Ordinal))
            || globals.Any(a => a.StartsWith(raw + "@", StringComparison.Ordinal)))
        {
            return false;
        }

        var nets = string.Join(" or ", MainNets.Select(n => $"(ip.DstAddr>={n.From} and ip.DstAddr<={n.To})"));
        var ours = $"(!tcp or tcp.Syn or tcp.Rst or tcp.Fin or tcp.PayloadLength>0 or (outbound and tcp.DstPort==443 and ({nets})))";

        int index = globals.FindIndex(a => a.StartsWith(raw, StringComparison.Ordinal));

        if (index >= 0)
            globals[index] = $"{raw}({globals[index][raw.Length..]}) and {ours}";
        else
            globals.Add(raw + ours);

        if (!globals.Contains("--wf-tcp-empty=1"))
            globals.Add("--wf-tcp-empty=1");

        return true;
    }

    /// <summary>Секция по имени узлов видео: TCP, списки googlevideo или youtube, не «пропустить».</summary>
    public static bool IsVideoNameSection(ZapretSection section) =>
        !section.IsPassThrough
        && section.DesyncRecipes.Count > 0
        && section.RawArguments.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal))
        && section.HostListPaths.Any(p => Bare(p) is "googlevideo.txt" or "youtube.txt");

    private static string Bare(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        return name.StartsWith("nz-", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
    }

    private static ZapretSection WithNameStep(ZapretSection section, string step)
    {
        var raw = section.RawArguments.ToList();
        int first = raw.FindIndex(a => a.StartsWith("--lua-desync=", StringComparison.Ordinal));
        raw.Insert(first < 0 ? raw.Count : first, "--lua-desync=" + step);

        return section with { RawArguments = raw, DesyncRecipes = [step, .. section.DesyncRecipes] };
    }

    private static ZapretSection SynSection(string name, string cidr, string step, bool all)
    {
        var raw = new List<string> { $"--name={name}", "--filter-tcp=443", $"--ipset-ip={cidr}" };

        // Пересчёт окна видит каждый исходящий пакет, пустые тоже (ScaleRamp).
        if (all)
            raw.AddRange(["--out-range=a", "--payload=all"]);

        raw.Add("--lua-desync=" + step);

        return new ZapretSection
        {
            Name = name,
            HostListPaths = [],
            InlineDomains = [],
            IpSetPaths = [],
            DesyncRecipes = [step],
            RawArguments = raw,
        };
    }

    /// <summary>
    /// QUIC к узлам видео — выбросить первые пакеты: плеер сразу уходит на TCP.
    /// </summary>
    /// <remarks>
    /// Так делает пресет loop-uh «Default + YouTube no QUIC + wssize» (Zapret GUI 2.44). Там,
    /// где QUIC к узлам мёртв (обсуждение №63: 8 из 30), браузер иначе ждёт его, прежде
    /// чем пойти по TCP. Где QUIC живой (дом владельца, 14.11: 21 из 30, видео 4K по h3) —
    /// не включается: автонастройка решает по замеру. Только узлы видео, не страница:
    /// QUIC к www.youtube.com не трогается.
    /// </remarks>
    private static ZapretSection DropQuicSection()
    {
        const string name = "YouTube: QUIC к узлам видео — выброшен";
        const string step = "drop";

        return new ZapretSection
        {
            Name = name,
            HostListPaths = ["lists/googlevideo.txt"],
            InlineDomains = [],
            IpSetPaths = [],
            DesyncRecipes = [step],
            RawArguments =
            [
                $"--name={name}", "--filter-udp=443", "--filter-l7=quic", "--hostlist=lists/googlevideo.txt",
                "--out-range=-n8", "--payload=all", "--lua-desync=" + step,
            ],
        };
    }
}
