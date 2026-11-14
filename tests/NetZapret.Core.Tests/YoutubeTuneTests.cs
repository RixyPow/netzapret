using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Автонастройка YouTube (14.11): решение по замеру узлов и надстройка к любому пресету.
/// Картины — настоящие замеры: дом владельца 14.11, обсуждение №63 (Сибирь), Тула 11.11.
/// </summary>
public sealed class YoutubeTuneTests
{
    private static VideoNode Node(int ping, int tcp, int quic, NodeFilter filter = NodeFilter.GoogleOnly, int quicTries = 3) => new()
    {
        Host = $"rr1---sn-n{ping}x{tcp}{quic}{(int)filter}{VideoNodes.Zone}",
        Address = "173.194.10.38",
        Tries = 3,
        Tcp = tcp,
        QuicTries = quicTries,
        Quic = quic,
        Filter = filter,
        PingMs = ping,
    };

    private static VideoNodesReport Report(params VideoNode[] nodes) => new() { Nodes = nodes, Desync = true };

    /// <summary>Дом 14.11: узлы под фильтром, пинг 21–43, QUIC живой.</summary>
    [Fact]
    public void HomeGetsTheRecipeAWiderWindowAndKeepsQuic()
    {
        var decision = YoutubeTuner.Decide(Report(
            Node(42, 3, 2), Node(43, 2, 2), Node(22, 3, 1), Node(22, 2, 1), Node(22, 2, 3),
            Node(23, 3, 1), Node(43, 3, 2), Node(20, 1, 3),
            Node(43, 3, 3, NodeFilter.Open), Node(43, 2, 3, NodeFilter.Open)), recipeOn: true);

        var tune = Assert.IsType<YoutubeTune>(decision.Tune);
        Assert.True(tune.Nodes);
        Assert.True(tune.Doubled);
        Assert.Equal(41, tune.FarMs);
        Assert.False(tune.DropQuic);
        Assert.False(decision.NothingLeft);
    }

    /// <summary>№63: все узлы далеко, QUIC почти мёртв — выбросить его, окно шире.</summary>
    [Fact]
    public void FarNodesWithDeadQuicDropQuic()
    {
        var decision = YoutubeTuner.Decide(Report(
            Node(69, 1, 0, NodeFilter.Closed), Node(70, 1, 0, NodeFilter.Closed), Node(47, 3, 1, NodeFilter.Closed),
            Node(51, 2, 0), Node(47, 2, 0), Node(49, 3, 1), Node(75, 1, 0, NodeFilter.Closed), Node(52, 2, 0),
            Node(73, 3, 3, NodeFilter.Open), Node(74, 3, 3, NodeFilter.Open)), recipeOn: true);

        var tune = Assert.IsType<YoutubeTune>(decision.Tune);
        Assert.True(tune.Nodes);
        Assert.True(tune.Doubled);
        Assert.True(tune.DropQuic);
        Assert.False(decision.NothingLeft);
    }

    /// <summary>Фильтра нет и всё отвечает — рецепт не нужен.</summary>
    [Fact]
    public void OpenNodesNeedNothing()
    {
        var nodes = Enumerable.Range(0, 6).Select(i => Node(20 + i, 3, 3, NodeFilter.Open)).ToArray();
        var tune = Assert.IsType<YoutubeTune>(YoutubeTuner.Decide(Report(nodes), recipeOn: false).Tune);

        Assert.False(tune.Nodes);
        Assert.False(tune.Doubled);
        Assert.False(tune.DropQuic);
        Assert.False(tune.Active);
    }

    /// <summary>Тула 11.11: рецепт работал, узлы под фильтром 0 из 24 — десинку нечего предложить.</summary>
    [Fact]
    public void ClosedEvenWithTheRecipeLeavesNothing()
    {
        var nodes = new[] { 44, 47, 24, 28, 25, 24, 45, 29 }.Select(p => Node(p, 0, 0, NodeFilter.Closed, quicTries: 0))
            .Append(Node(45, 2, 0, NodeFilter.Open, quicTries: 0)).Append(Node(44, 2, 0, NodeFilter.Open, quicTries: 0)).ToArray();

        var decision = YoutubeTuner.Decide(Report(nodes), recipeOn: true);

        Assert.True(decision.NothingLeft);
        Assert.Contains(decision.Reasons, r => r.Contains("VPN", StringComparison.Ordinal));

        // Без рецепта во время замера VPN не предлагается: сперва — рецепт.
        Assert.False(YoutubeTuner.Decide(Report(nodes), recipeOn: false).NothingLeft);
    }

    /// <summary>
    /// Рецепт в самом пресете (YT FIX): не обещать того, что надстройка не применит (14.11 —
    /// три решения на экране, строка winws2 та же).
    /// </summary>
    [Fact]
    public void APresetWithItsOwnRecipeIsNotPromisedAnything()
    {
        var decision = YoutubeTuner.Decide(Report(
            Node(42, 3, 2), Node(43, 2, 2), Node(22, 3, 1), Node(22, 2, 1), Node(22, 2, 3),
            Node(43, 3, 3, NodeFilter.Open), Node(43, 2, 3, NodeFilter.Open)), recipeOn: true, presetHasRecipe: true);

        var tune = Assert.IsType<YoutubeTune>(decision.Tune);
        Assert.False(tune.Nodes);
        Assert.False(tune.Doubled);
        Assert.False(tune.Active);
        Assert.Contains(decision.Choices, c => c.Verdict == "уже в пресете");
        Assert.DoesNotContain(decision.Choices, c => c.What == "Малое окно");
    }

    [Fact]
    public void TooFewNodesDecideNothing() =>
        Assert.Null(YoutubeTuner.Decide(Report(Node(20, 3, 3), Node(21, 3, 3)), recipeOn: false).Tune);

    private static ZapretPreset Shipped(string name) =>
        new PresetReader().Load(Path.Combine(Repository()!, "presets", name + ".txt"));

    private static string? Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetZapret.sln")))
                return dir.FullName;
        }

        return null;
    }

    /// <summary>Надстройка на V13 Lite даёт то же, что несёт пресет YT FIX.</summary>
    [Fact]
    public void TheAddonTurnsAnyPresetIntoYtFix()
    {
        if (Repository() is null)
            return;

        var tune = new YoutubeTune { Nodes = true, Doubled = true, FarMs = 41 };
        var preset = YoutubeAddon.Apply(Shipped("Universal V13 Lite"), tune);

        Assert.Contains(YoutubeAddon.LuaInit, preset.GlobalArguments);
        Assert.Contains("--wf-tcp-empty=1", preset.GlobalArguments);
        Assert.Contains(preset.GlobalArguments, a => a.StartsWith("--wf-raw-filter=", StringComparison.Ordinal) && a.Contains("74.125.0.0"));

        // Шаг по имени — первым в секциях googlevideo и youtube.
        var named = preset.Sections.Where(YoutubeAddon.IsVideoNameSection).ToList();
        Assert.NotEmpty(named);
        Assert.All(named, s => Assert.Equal("nz_first2:reset:far=41", s.DesyncRecipes[0]));

        // Секции SYN — прямо над IP fallback.
        int fallback = preset.Sections.ToList().FindIndex(s => s.Name.Contains("IP fallback", StringComparison.Ordinal));
        Assert.Equal("nz_first2:syn:far=41:scale=1", preset.Sections[fallback - 2].DesyncRecipes[0]);
        Assert.Equal("nz_first2:syn:far=41", preset.Sections[fallback - 1].DesyncRecipes[0]);

        // Строка: пересчёт окна встал в секции, свой рецепт уносит шаг по имени.
        Assert.Contains(ScaleRamp.Step, WinwsCommandLine.Build(preset));
        Assert.Equal(["nz_first2:reset:far=41"], PresetCompanions.ForDomains(preset, Path.Combine(Repository()!, "vendor", "zapret-gui"), ["rr1---sn-abc.googlevideo.com"]));
    }

    /// <summary>Пресет со своим рецептом (YT FIX) решил за себя; QUIC выбрасывается и у него.</summary>
    [Fact]
    public void YtFixKeepsItsOwnRecipeButQuicCanStillGo()
    {
        if (Repository() is null)
            return;

        var original = Shipped("Universal V13 YT FIX");
        var preset = YoutubeAddon.Apply(original, new YoutubeTune { Nodes = true, Doubled = true, DropQuic = true });

        Assert.Equal(original.GlobalArguments, preset.GlobalArguments);
        Assert.Equal(original.Sections.Count + 1, preset.Sections.Count);
        Assert.Equal(["drop"], preset.Sections[0].DesyncRecipes);
        Assert.Contains("--filter-udp=443", preset.Sections[0].RawArguments);
    }

    /// <summary>
    /// «Записать» без изменений строки — без перезапуска (14.11: перезапуск и перезамер
    /// показали разброс 27 → 21 как «стало хуже»).
    /// </summary>
    [Fact]
    public void OnlyARealChangeOfTheLineCounts()
    {
        Assert.True(YoutubeAddon.SameLine(null, new YoutubeTune()));
        Assert.True(YoutubeAddon.SameLine(new YoutubeTune { Doubled = true, FarMs = 41 }, null));
        Assert.True(YoutubeAddon.SameLine(new YoutubeTune { Nodes = true, FarMs = 41 }, new YoutubeTune { Nodes = true, FarMs = 41, Summary = "другой замер" }));
        Assert.False(YoutubeAddon.SameLine(null, new YoutubeTune { DropQuic = true }));
        Assert.False(YoutubeAddon.SameLine(new YoutubeTune { Nodes = true, FarMs = 41 }, new YoutubeTune { Nodes = true, FarMs = 20 }));

        // YT FIX: рецепт пресета, надстройка его не трогает — «рецепт, окно вдвое» и «ничего» одно.
        Assert.True(YoutubeAddon.SameLine(new YoutubeTune { Nodes = true, Doubled = true, FarMs = 41 }, new YoutubeTune(), presetHasRecipe: true));
        Assert.False(YoutubeAddon.SameLine(new YoutubeTune { Nodes = true }, new YoutubeTune { DropQuic = true }, presetHasRecipe: true));
    }

    [Fact]
    public void NoTuneNoChange()
    {
        if (Repository() is null)
            return;

        var original = Shipped("Universal V13 Lite");

        Assert.Same(original, YoutubeAddon.Apply(original, null));
        Assert.Same(original, YoutubeAddon.Apply(original, new YoutubeTune()));
    }
}
