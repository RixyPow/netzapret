using System.Diagnostics;
using NetZapret.Core.Programs;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Запущенные программы для выбора в «Маршрутах» (01.10).</summary>
public sealed class RunningProgramsTests
{
    /// <summary>Сам тестовый процесс — запущенная программа с известным путём.</summary>
    [Fact]
    public void The_running_test_process_is_listed_with_its_path()
    {
        using var self = Process.GetCurrentProcess();
        var path = self.MainModule!.FileName;

        Assert.Equal(path, RunningPrograms.PathOf(self.Id), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(RunningPrograms.List(), p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)
            && p.Name == Path.GetFileName(path));
    }

    [Fact]
    public void Each_path_is_listed_once()
    {
        var list = RunningPrograms.List();

        Assert.Equal(list.Count, list.Select(p => p.Path.ToUpperInvariant()).Distinct().Count());
    }

    [Theory]
    [InlineData("diablo", true)]          // по имени, без учёта регистра
    [InlineData("Battle.net", true)]      // по пути
    [InlineData("", true)]                // пусто — подходит всё
    [InlineData("claude", false)]
    public void Search_matches_name_or_path(string query, bool expected)
    {
        var program = new RunningProgram("Diablo IV.exe", @"C:\Program Files (x86)\Battle.net\Diablo IV\Diablo IV.exe");

        Assert.Equal(expected, RunningPrograms.Matches(program, query));
    }
}
