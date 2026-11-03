using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Обновление пинов (28.09): меняется только то, что умерло целиком.
/// </summary>
public sealed class PinRefreshTests
{
    [Fact]
    public void Only_names_with_every_address_dead_are_replaced()
    {
        // Имя на двух адресах посредника живо, пока жив хоть один: Windows
        // дойдёт до второго. Менять его — трогать работающее.
        var pins = new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude.ai"] = ["87.228.47.201", "87.228.47.203"],
            ["chatgpt.com"] = ["87.228.47.204"],
            ["grok.com"] = ["87.228.47.204", "87.228.47.195"],
        };

        var alive = new Dictionary<string, bool>
        {
            ["87.228.47.201"] = false,
            ["87.228.47.203"] = true,
            ["87.228.47.204"] = false,
            ["87.228.47.195"] = false,
        };

        Assert.Equal(["chatgpt.com", "grok.com"], PinRefresh.Plan(pins, alive));
    }

    [Fact]
    public void Unchecked_address_is_not_called_dead()
    {
        // Заглушки (127.0.0.1) не проверяются вовсе — и не должны считаться мёртвыми.
        var pins = new Dictionary<string, IReadOnlyList<string>> { ["image.tmdb.org"] = ["127.0.0.1"] };

        Assert.Empty(PinRefresh.Plan(pins, new Dictionary<string, bool>()));
    }

    [Fact]
    public void Repin_keeps_the_block_note_and_other_names()
    {
        var file = Path.Combine(Path.GetTempPath(), "nz-hosts-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllLines(file,
            [
                "# hosts",
                HostsEditor.BlockBegin,
                "# Claude — XBOX DNS",
                "# Записи ведёт NetZapret. Правьте их в окне, «Файл hosts»: при следующей",
                "# записи всё, что дописано сюда руками, будет потеряно.",
                "87.228.47.204 chatgpt.com",
                "35.186.224.24 login5.spotify.com",
                HostsEditor.BlockEnd,
                "",
                "72.56.93.144 chatgpt.com",
            ]);

            HostsEditor.Repin(new Dictionary<string, IReadOnlyList<string>>
            {
                ["chatgpt.com"] = ["87.228.47.201", "87.228.47.203"],
            }, file);

            var pins = HostsEditor.PinsAll(file);
            var text = File.ReadAllText(file);

            Assert.Equal(["87.228.47.201", "87.228.47.203"], pins["chatgpt.com"]);
            Assert.Equal(["35.186.224.24"], pins["login5.spotify.com"]);
            Assert.Contains("# Claude — XBOX DNS", text);
            Assert.DoesNotContain("87.228.47.204", text);

            // Чужая строка ниже блока — не наша, и её не трогаем.
            Assert.Contains("72.56.93.144 chatgpt.com", text);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*"))
                File.Delete(f);
        }
    }

    [Fact]
    public void A_day_passes_before_the_next_check_by_itself()
    {
        var stamp = Path.Combine(Path.GetTempPath(), $"nz-pin-refresh-{Guid.NewGuid():N}.txt");
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(3));

        try
        {
            // Отметки нет — проверять пора.
            Assert.True(PinRefresh.IsDue(now, stamp));

            PinRefresh.Mark(now, stamp);

            Assert.False(PinRefresh.IsDue(now.AddHours(23), stamp));
            Assert.True(PinRefresh.IsDue(now.AddHours(24), stamp));

            // Часы ушли назад (отметка из будущего) — не ждать сутки лишнего.
            Assert.True(PinRefresh.IsDue(now.AddHours(-2), stamp));

            File.WriteAllText(stamp, "испорчено");
            Assert.True(PinRefresh.IsDue(now, stamp));
        }
        finally
        {
            File.Delete(stamp);
        }
    }

    [Fact]
    public void Candidates_keep_the_order_of_intermediaries_and_drop_sinkholes()
    {
        // Так 03.11 ответили на claude.ai: XBOX — 0.0.0.0, Comss и GeoHide EU —
        // свои прокси. Заглушка не кандидат, повтор — тоже.
        var found = IntermediaryDns.Candidates(
        [
            ("XBOX DNS", [System.Net.IPAddress.Parse("0.0.0.0")]),
            ("Comss DNS", [System.Net.IPAddress.Parse("206.223.251.18"), System.Net.IPAddress.Parse("157.22.181.209")]),
            ("GeoHide EU", [System.Net.IPAddress.Parse("45.13.237.213"), System.Net.IPAddress.Parse("206.223.251.18")]),
        ]);

        Assert.Equal(["206.223.251.18", "157.22.181.209", "45.13.237.213"], found.Select(c => c.Address));
        Assert.Equal("GeoHide EU, сейчас", found[2].Label);
    }

    [Fact]
    public void Every_intermediary_is_asked_over_doh_with_a_fallback_address()
    {
        // Обычный DNS у Comss, AstraCat и DNS-AI 03.11 молчал: спрашивать только по DoH,
        // а встроенные адреса нужны, когда имя сервера не узналось.
        Assert.All(IntermediaryDns.Servers, s =>
        {
            Assert.Contains('.', s.DohName);
            Assert.NotEmpty(s.Fallback);
            Assert.StartsWith("/", s.Path);
        });

        Assert.DoesNotContain(IntermediaryDns.Servers, s => s.Name.Contains("XBOX", StringComparison.Ordinal));
        Assert.True(new AppSettings().AutoRepin);
    }

    [Fact]
    public void A_real_address_given_to_one_site_is_not_a_proxy()
    {
        // Comss 03.11: свой прокси 206.223.251.18 — claude.ai, chatgpt.com, grok.com;
        // на Gemini, AI Studio и googleapis — настоящий Google, на Spotify — Spotify.
        static IReadOnlyList<System.Net.IPAddress> A(params string[] a) => a.Select(System.Net.IPAddress.Parse).ToList();

        var proxies = IntermediaryDns.Proxies(new Dictionary<string, IReadOnlyList<System.Net.IPAddress>>
        {
            ["claude.ai"] = A("206.223.251.18"),
            ["chatgpt.com"] = A("206.223.251.18"),
            ["grok.com"] = A("206.223.251.18"),
            ["gemini.google.com"] = A("142.251.157.2"),
            ["aistudio.google.com"] = A("142.251.157.2"),
            ["generativelanguage.googleapis.com"] = A("142.251.157.2"),
            ["open.spotify.com"] = A("35.186.224.24"),
        });

        Assert.Equal(["206.223.251.18"], proxies);
    }
}
