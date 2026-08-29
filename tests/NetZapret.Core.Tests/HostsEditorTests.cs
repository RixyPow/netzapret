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

    /// <summary>Выключение — комментарий, а не удаление.</summary>
    /// <remarks>
    /// Запись должна возвращаться тем же движением, каким выключена. Файл
    /// общий: часть строк ставит редактор Zapret, часть человек руками,
    /// и удаление лишает возможности передумать.
    /// </remarks>
    [Fact]
    public void DisablingCommentsOutAndKeepsTheLine()
    {
        Write("1.2.3.4 example.com");

        var entry = Assert.Single(HostsEditor.Parse(_path));
        HostsEditor.SetEnabled([entry.Line], enabled: false, _path);

        var after = Assert.Single(HostsEditor.Parse(_path));

        Assert.False(after.Enabled);
        Assert.Equal("example.com", Assert.Single(after.Names));
        Assert.Contains("example.com", File.ReadAllText(_path));
    }

    [Fact]
    public void EnablingRemovesTheComment()
    {
        Write("# 1.2.3.4 example.com");

        var entry = Assert.Single(HostsEditor.Parse(_path));
        HostsEditor.SetEnabled([entry.Line], enabled: true, _path);

        Assert.True(Assert.Single(HostsEditor.Parse(_path)).Enabled);
    }

    /// <summary>Соседние строки остаются нетронутыми.</summary>
    /// <remarks>
    /// Самое важное здесь. Файл ведём не мы одни, и переписать его «как мы
    /// понимаем формат» значит стереть то, чего мы не поняли.
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
        HostsEditor.SetEnabled([target.Line], enabled: false, _path);

        var text = File.ReadAllText(_path);

        Assert.Contains("# Copyright (c) 1993-2009 Microsoft Corp.", text);
        Assert.Contains("5.6.7.8 second.example   # поставлено Zapret GUI", text);
    }

    /// <summary>Перед правкой остаётся копия.</summary>
    [Fact]
    public void EditLeavesABackup()
    {
        Write("1.2.3.4 example.com");

        var backup = HostsEditor.SetEnabled([0], enabled: false, _path);

        Assert.True(File.Exists(backup));
        Assert.Contains("1.2.3.4 example.com", File.ReadAllText(backup));
    }
}
