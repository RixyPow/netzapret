using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Удаление строк из файла hosts.
/// </summary>
/// <remarks>
/// Заведено 17.09 по решению владельца взамен прежнего «чужое только
/// выключаем». Файл системный и общий, отменить удаление нажатием нельзя,
/// и всё, что отличает его от потери, — копия перед правкой. Поэтому здесь
/// проверяется не только то, что нужная строка ушла, но и то, что остальной
/// файл остался дословно прежним.
/// </remarks>
public sealed class HostsRemoveTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"netzapret-hosts-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(
            Path.GetDirectoryName(_file)!, Path.GetFileNameWithoutExtension(_file) + "*"))
        {
            try { File.Delete(f); } catch (Exception) { }
        }
    }

    private const string Sample = """
        # Чужой комментарий, который трогать нельзя
        127.0.0.1 localhost

        72.56.93.144 canva.com
        # 1.2.3.4 disabled.example   # выключено кем-то
        149.154.167.220 t.me api.telegram.org
        """;

    private IReadOnlyList<HostsEntry> Write()
    {
        File.WriteAllText(_file, Sample);

        return HostsEditor.Parse(_file);
    }

    [Fact]
    public void The_named_line_goes_and_the_rest_stays()
    {
        var before = Write();
        var canva = before.Single(e => e.Names.Contains("canva.com"));

        HostsEditor.Remove([canva.Line], _file);

        var after = File.ReadAllLines(_file);

        Assert.DoesNotContain(after, l => l.Contains("canva.com"));

        // Остальное — дословно, вместе с чужим комментарием, пустой строкой
        // и выключенной записью. Переписать «как мы понимаем формат» значило бы
        // стереть то, чего мы не поняли.
        Assert.Contains(after, l => l == "# Чужой комментарий, который трогать нельзя");
        Assert.Contains(after, l => l == "127.0.0.1 localhost");
        Assert.Contains(after, l => l.Contains("disabled.example"));
        Assert.Contains(after, l => l.Contains("t.me"));
        Assert.Contains(after, l => l.Length == 0);
    }

    /// <summary>
    /// Копия делается всегда и содержит прежний файл целиком.
    /// </summary>
    /// <remarks>
    /// Единственный способ отката: отменить удаление нажатием нельзя,
    /// а вернуть файл из копии — можно.
    /// </remarks>
    [Fact]
    public void A_copy_is_made_before_the_edit()
    {
        var before = Write();
        var line = before.Single(e => e.Names.Contains("canva.com")).Line;

        var backup = HostsEditor.Remove([line], _file);

        Assert.True(File.Exists(backup));
        Assert.Equal(Sample.ReplaceLineEndings(), File.ReadAllText(backup).ReplaceLineEndings());
    }

    /// <summary>Удаляется строка с тем номером, а не «такая же».</summary>
    /// <remarks>
    /// Две записи могут совпадать дословно, и сравнение по содержимому
    /// сняло бы не ту. Проверяется на строке с двумя именами: уходит она
    /// целиком, и оба имени вместе с ней.
    /// </remarks>
    [Fact]
    public void A_line_with_two_names_goes_whole()
    {
        var before = Write();
        var telegram = before.Single(e => e.Names.Contains("t.me"));

        Assert.Equal(2, telegram.Names.Count);

        HostsEditor.Remove([telegram.Line], _file);

        var after = File.ReadAllText(_file);

        Assert.DoesNotContain("t.me", after);
        Assert.DoesNotContain("api.telegram.org", after);
        Assert.Contains("canva.com", after);
    }

    /// <summary>Несуществующий номер ничего не рушит.</summary>
    [Fact]
    public void An_impossible_line_changes_nothing()
    {
        Write();

        HostsEditor.Remove([999, -1], _file);

        Assert.Equal(Sample.ReplaceLineEndings(), File.ReadAllText(_file).ReplaceLineEndings());
    }

    /// <summary>Пустой список — тоже ничего.</summary>
    [Fact]
    public void An_empty_request_changes_nothing()
    {
        Write();

        HostsEditor.Remove([], _file);

        Assert.Equal(Sample.ReplaceLineEndings(), File.ReadAllText(_file).ReplaceLineEndings());
    }
}
