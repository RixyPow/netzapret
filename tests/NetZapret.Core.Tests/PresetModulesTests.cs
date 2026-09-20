using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подключение модуля Lua дописывает пресету одну строку — и только её.
/// </summary>
public sealed class PresetModulesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nz-modules-" + Guid.NewGuid().ToString("N")[..8]);

    public PresetModulesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string text, string ending = "\r\n")
    {
        var path = Path.Combine(_root, "preset.txt");

        File.WriteAllText(path, text.ReplaceLineEndings(ending));

        return path;
    }

    private const string Sample = """
        # Пресет
        --wf-tcp-out=80,443
        --lua-init=@lua/zapret-lib.lua
        --lua-init=@lua/zapret-antidpi.lua
        --blob=tls_max:@bin/tls_clienthello_max_ru.bin
        --new
        --name=discord
        --lua-desync=multidisorder:pos=1,host+2
        """;

    [Fact]
    public void The_line_goes_after_the_last_one_of_its_kind()
    {
        // Порядок значим: модули подключаются по очереди, и тот, что
        // опирается на предыдущие, обязан идти после них.
        var path = Write(Sample);

        Assert.Equal(PresetModules.Result.Connected,
            PresetModules.Connect(path, "zapret-16kb.lua"));

        var lines = File.ReadAllLines(path);
        int added = Array.FindIndex(lines, l => l.Contains("zapret-16kb.lua"));
        int antidpi = Array.FindIndex(lines, l => l.Contains("zapret-antidpi.lua"));
        int blob = Array.FindIndex(lines, l => l.StartsWith("--blob=", StringComparison.Ordinal));

        Assert.Equal(antidpi + 1, added);
        Assert.Equal(added + 1, blob);
    }

    [Fact]
    public void Nothing_else_in_the_file_moves()
    {
        var path = Write(Sample);
        var before = File.ReadAllLines(path);

        PresetModules.Connect(path, "zapret-16kb.lua");

        var after = File.ReadAllLines(path).Where(l => !l.Contains("zapret-16kb.lua"));

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_module_already_connected_leaves_the_file_alone()
    {
        var path = Write(Sample);
        var before = File.ReadAllBytes(path);

        Assert.Equal(PresetModules.Result.Already,
            PresetModules.Connect(path, "zapret-antidpi.lua"));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Without_a_single_init_line_there_is_nowhere_to_put_it()
    {
        // Не в начало файла наугад: пресет без --lua-init устроен иначе,
        // чем мы полагаем, и угаданное место скорее сломает его, чем починит.
        var path = Write("""
            # Пресет
            --wf-tcp-out=80,443
            --new
            --lua-desync=multidisorder:pos=1
            """);

        var before = File.ReadAllBytes(path);

        Assert.Equal(PresetModules.Result.NoPlace,
            PresetModules.Connect(path, "zapret-16kb.lua"));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void The_line_ending_of_the_file_is_kept()
    {
        // Пресеты приходят из Zapret с CRLF. Строка с одним LF делает файл
        // смешанным: разбирается, но в чужом редакторе выглядит испорченным.
        var crlf = Write(Sample, "\r\n");
        PresetModules.Connect(crlf, "zapret-16kb.lua");
        Assert.DoesNotContain("\n", File.ReadAllText(crlf).Replace("\r\n", string.Empty));

        var lf = Write(Sample, "\n");
        PresetModules.Connect(lf, "zapret-16kb.lua");
        Assert.DoesNotContain("\r", File.ReadAllText(lf));
    }

    [Fact]
    public void Connecting_twice_adds_the_line_once()
    {
        var path = Write(Sample);

        PresetModules.Connect(path, "zapret-16kb.lua");
        PresetModules.Connect(path, "zapret-16kb.lua");

        Assert.Equal(1, File.ReadAllLines(path).Count(l => l.Contains("zapret-16kb.lua")));
    }

    [Fact]
    public void The_file_does_not_grow_an_empty_line_each_time()
    {
        // Хвост после завершающего перевода строки — не строка файла.
        // Принимать его за строку значило бы дописывать по пустой
        // при каждом подключении.
        var path = Write(Sample);
        int before = File.ReadAllLines(path).Length;

        PresetModules.Connect(path, "zapret-16kb.lua");
        PresetModules.Connect(path, "zapret-obfs.lua");

        Assert.Equal(before + 2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void The_preset_still_reads_after_the_edit()
    {
        // Смысл всей правки в том, чтобы пресет остался пресетом.
        var path = Write(Sample);

        PresetModules.Connect(path, "zapret-16kb.lua");

        var preset = new PresetReader().Load(path);

        Assert.Contains("zapret-16kb.lua", LuaModules.DeclaredBy(preset));
        Assert.Contains("tls_max", LuaModules.BlobsOf(preset));
        Assert.Single(preset.ActiveSections);
    }
}
