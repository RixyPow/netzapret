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

    /// <summary>
    /// Найденное поиском уходит по имени, а соседнее имя в той же строке остаётся.
    /// </summary>
    /// <remarks>
    /// Поиск по «t.me» не выбирал api.telegram.org, и удалить его заодно
    /// значило бы снять то, о чём человек не просил.
    /// </remarks>
    [Fact]
    public void A_found_name_goes_and_its_neighbour_stays()
    {
        var before = Write();
        var telegram = before.Single(e => e.Names.Contains("t.me"));

        var (_, removed) = HostsEditor.RemoveNames([(telegram.Line, "t.me")], _file);

        var after = File.ReadAllLines(_file);

        Assert.Equal(1, removed);
        Assert.Contains(after, l => l == "149.154.167.220 api.telegram.org");
        Assert.DoesNotContain(after, l => l.Split(' ').Contains("t.me"));
    }

    /// <summary>Выключенная запись остаётся выключенной, подпись — на месте.</summary>
    [Fact]
    public void A_disabled_line_keeps_its_hash_and_note()
    {
        File.WriteAllText(_file, "# 1.2.3.4 a.example b.example # от кого-то\r\n");
        var entry = HostsEditor.Parse(_file).Single();

        HostsEditor.RemoveNames([(entry.Line, "a.example")], _file);

        Assert.Equal("# 1.2.3.4 b.example # от кого-то", File.ReadAllLines(_file).Single());
    }

    /// <summary>Строка без оставшихся имён уходит, прочий файл — дословно.</summary>
    [Fact]
    public void Many_lines_go_in_one_pass_with_one_copy()
    {
        var before = Write();
        var targets = before
            .Where(e => e.Names.Contains("canva.com") || e.Names.Contains("t.me"))
            .SelectMany(e => e.Names.Select(n => (e.Line, n)))
            .ToList();

        var (backup, removed) = HostsEditor.RemoveNames(targets, _file);

        Assert.Equal(3, removed);
        Assert.Equal(
            """
            # Чужой комментарий, который трогать нельзя
            127.0.0.1 localhost

            # 1.2.3.4 disabled.example   # выключено кем-то
            """.ReplaceLineEndings() + Environment.NewLine,
            File.ReadAllText(_file).ReplaceLineEndings());

        Assert.Equal(Sample.ReplaceLineEndings(), File.ReadAllText(backup).ReplaceLineEndings());
    }

    /// <summary>
    /// Номер, под которым имени уже нет, не трогает строку.
    /// </summary>
    /// <remarks>
    /// Между показом и нажатием файл мог переписать кто угодно; по старому
    /// номеру лежит чужая строка, и снимать её нельзя.
    /// </remarks>
    [Fact]
    public void A_stale_line_number_is_skipped()
    {
        var before = Write();
        var canva = before.Single(e => e.Names.Contains("canva.com"));

        var (_, removed) = HostsEditor.RemoveNames([(canva.Line, "notion.so")], _file);

        Assert.Equal(0, removed);
        Assert.Contains("canva.com", File.ReadAllText(_file));
    }

    /// <summary>Наш блок, опустев, уходит вместе с отметками.</summary>
    [Fact]
    public void An_emptied_block_of_ours_goes_whole()
    {
        File.WriteAllText(_file, string.Join("\r\n",
            "127.0.0.1 localhost",
            HostsEditor.BlockBegin,
            "72.56.93.144 jetbrains.com",
            HostsEditor.BlockEnd,
            "72.56.93.144 academy.jetbrains.com") + "\r\n");

        var targets = HostsEditor.Parse(_file)
            .Where(e => e.Names.Any(n => n.Contains("jetbrains")))
            .SelectMany(e => e.Names.Select(n => (e.Line, n)))
            .ToList();

        HostsEditor.RemoveNames(targets, _file);

        Assert.Equal(["127.0.0.1 localhost"], File.ReadAllLines(_file));
    }
}
