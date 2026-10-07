using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Разбор и правка файла hosts.
/// </summary>
/// <remarks>
/// Проверяется на копии во временной папке, а не на системном файле:
/// испортить его тестом означало бы оставить машину без сети, и никакая
/// польза от проверки этого не стоит.
/// </remarks>
public class HostsEditorTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"netzapret-hosts-{Guid.NewGuid():N}.txt");

    private void Write(params string[] lines) => File.WriteAllLines(_path, lines);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(
            Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ParsesAddressAndNames()
    {
        Write("72.56.93.144 chatgpt.com openai.com");

        var entry = Assert.Single(HostsEditor.Parse(_path));

        Assert.Equal("72.56.93.144", entry.Address);
        Assert.Equal(["chatgpt.com", "openai.com"], entry.Names);
        Assert.True(entry.Enabled);
    }

    /// <summary>Выключенная запись — не то же, что отсутствующая.</summary>
    /// <remarks>
    /// Ради этого разбор и отделён от <c>HostsFile.Read</c>: тот отвечает
    /// на вопрос «во что разрешится имя», и закомментированные строки ему
    /// не нужны. Редактору без них нечего показывать и нечего возвращать.
    /// </remarks>
    [Fact]
    public void CommentedEntriesAreParsedAsDisabled()
    {
        Write("# 1.2.3.4 example.com");

        var entry = Assert.Single(HostsEditor.Parse(_path));

        Assert.False(entry.Enabled);
        Assert.Equal("example.com", Assert.Single(entry.Names));
    }

    /// <summary>Настоящий комментарий записью не считается.</summary>
    [Fact]
    public void ProseCommentsAreNotEntries()
    {
        Write("# Copyright (c) 1993-2009 Microsoft Corp.", "# This is a sample HOSTS file");

        Assert.Empty(HostsEditor.Parse(_path));
    }

    [Fact]
    public void TrailingCommentBecomesTheNote()
    {
        Write("1.2.3.4 example.com  # netzapret");

        Assert.Equal("netzapret", Assert.Single(HostsEditor.Parse(_path)).Note);
    }

    /// <summary>Соседние строки остаются нетронутыми.</summary>
    /// <remarks>
    /// Самое важное здесь. Файл ведём не мы одни, и переписать его «как мы
    /// понимаем формат» значит стереть то, чего мы не поняли. Прежде
    /// проверялось на выключении строки; выключение ушло с консолью 23.09,
    /// а запись у удаления та же.
    /// </remarks>
    [Fact]
    public void OtherLinesSurviveUntouched()
    {
        Write(
            "# Copyright (c) 1993-2009 Microsoft Corp.",
            "1.2.3.4 first.example",
            "5.6.7.8 second.example   # поставлено Zapret GUI",
            "");

        var target = HostsEditor.Parse(_path).First(e => e.Names.Contains("first.example"));
        HostsEditor.Remove([target.Line], _path);

        var text = File.ReadAllText(_path);

        Assert.Contains("# Copyright (c) 1993-2009 Microsoft Corp.", text);
        Assert.Contains("5.6.7.8 second.example   # поставлено Zapret GUI", text);
        Assert.DoesNotContain("first.example", text);
    }

    /// <summary>Метка порядка байтов не накапливается.</summary>
    /// <remarks>
    /// Наблюдалось на живом файле: восемнадцать меток подряд в первой строке.
    /// Чтение оставляет метку как обычный символ, запись дописывала свою
    /// перед ней — и каждая правка добавляла ещё одну.
    /// </remarks>
    [Fact]
    public void ByteOrderMarksDoNotAccumulate()
    {
        File.WriteAllText(
            _path,
            "\uFEFF# comment\r\n1.2.3.4 a.example\r\n1.2.3.4 b.example\r\n1.2.3.4 c.example\r\n",
            new UTF8Encoding(true));

        foreach (var name in new[] { "a.example", "b.example" })
        {
            var entry = HostsEditor.Parse(_path).Single(e => e.Names.Contains(name));
            HostsEditor.Remove([entry.Line], _path);
        }

        var text = File.ReadAllText(_path);
        int marks = text.TakeWhile(c => c == '\uFEFF').Count();

        Assert.True(marks <= 1, $"меток порядка байтов накопилось {marks}");
        Assert.Contains("c.example", text);
    }

    /// <summary>Перед правкой остаётся копия.</summary>
    [Fact]
    public void EditLeavesABackup()
    {
        Write("1.2.3.4 example.com");

        var backup = HostsEditor.Remove([0], _path);

        Assert.True(File.Exists(backup));
        Assert.Contains("1.2.3.4 example.com", File.ReadAllText(backup));
    }

    /// <summary>
    /// Копии не копятся: остаются первая (hosts до NetZapret) и три последние.
    /// </summary>
    /// <remarks>
    /// 07.10 у пользователя рядом с hosts лежало шесть копий за 11 секунд,
    /// и их никто не убирал. Чужой файл с похожим именем не трогается.
    /// </remarks>
    [Fact]
    public void OldBackupsAreRemovedButTheFirstStays()
    {
        Write("1.2.3.4 example.com", "5.6.7.8 other.example");

        string Copy(string stamp)
        {
            var file = $"{_path}.netzapret-{stamp}.bak";
            File.WriteAllText(file, stamp);
            return file;
        }

        var first = Copy("20261001-010000");
        var middle = new[] { Copy("20261002-010000"), Copy("20261003-010000"), Copy("20261004-010000") };
        var recent = new[] { Copy("20261005-010000"), Copy("20261006-010000") };
        var foreign = _path + ".netzapret-notes.bakup";
        File.WriteAllText(foreign, "чужое");

        var backup = HostsEditor.Remove([0], _path);

        Assert.True(File.Exists(first), "первая копия — hosts до NetZapret — пропала");
        Assert.All(middle, file => Assert.False(File.Exists(file), $"осталась лишняя {Path.GetFileName(file)}"));
        Assert.All(recent, file => Assert.True(File.Exists(file), $"пропала недавняя {Path.GetFileName(file)}"));
        Assert.True(File.Exists(backup), "пропала только что сделанная копия");
        Assert.True(File.Exists(foreign), "тронут чужой файл");
        Assert.Equal(1 + HostsEditor.BackupsKept,
            Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".netzapret-*.bak")
                .Count(file => !file.EndsWith(".bakup", StringComparison.OrdinalIgnoreCase)));
    }
}
