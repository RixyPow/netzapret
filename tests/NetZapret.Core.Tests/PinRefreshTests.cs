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
}
