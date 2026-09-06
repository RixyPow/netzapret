using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Файл hosts переписал защитник.
/// </summary>
/// <remarks>
/// Найдено на живой машине: Kaspersky считает изменённый hosts признаком
/// заражения и заменяет файл своим. Прибитое исчезает целиком, ошибки
/// не возникает, и отличить это от «человек ничего не прибивал» можно
/// только по оставленной записке.
/// </remarks>
public sealed class HostsReplacedTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private string Written(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-hosts-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        _files.Add(path);

        return path;
    }

    /// <summary>Ровно тот файл, что пришёл с машины пользователя.</summary>
    [Fact]
    public void TheKasperskyNoticeIsRecognised()
    {
        var path = Written(
            "#This file has been replaced with its default version by Kaspersky Lab because of possible infection",
            "#",
            "#",
            "#",
            "127.0.0.1 localhost",
            "::1 localhost");

        Assert.Equal("Kaspersky", HostsEditor.WhoReplaced(path));
    }

    [Fact]
    public void TheRussianWordingIsRecognisedToo()
    {
        var path = Written(
            "# Файл заменён версией по умолчанию Лабораторией Касперского",
            "127.0.0.1 localhost");

        Assert.Equal("Kaspersky", HostsEditor.WhoReplaced(path));
    }

    /// <summary>Имени не знаем, но о самой замене сказать обязаны.</summary>
    [Fact]
    public void AnUnnamedGuardIsStillReported()
    {
        var path = Written(
            "# This file has been replaced with its default version",
            "127.0.0.1 localhost");

        Assert.Equal("антивирус", HostsEditor.WhoReplaced(path));
    }

    /// <summary>Одного упоминания мало — иначе сработает на своём же тексте.</summary>
    [Fact]
    public void MerelyNamingTheProductIsNotEnough()
    {
        var path = Written(
            "# Kaspersky ругается на эту запись, но она нужна",
            "1.2.3.4 example.com");

        Assert.Null(HostsEditor.WhoReplaced(path));
    }

    [Fact]
    public void AnOrdinaryFileIsLeftAlone()
    {
        var path = Written(
            "# Copyright (c) 1993-2009 Microsoft Corp.",
            "127.0.0.1 localhost",
            "1.2.3.4 example.com");

        Assert.Null(HostsEditor.WhoReplaced(path));
    }

    [Fact]
    public void AMissingFileIsNotAReplacedOne()
    {
        Assert.Null(HostsEditor.WhoReplaced(
            Path.Combine(Path.GetTempPath(), $"netzapret-none-{Guid.NewGuid():N}.txt")));
    }

    [Fact]
    public void OurBlockIsFoundWhenItIsThere()
    {
        var path = Written(
            HostsEditor.BlockBegin,
            "1.2.3.4 example.com",
            HostsEditor.BlockEnd);

        Assert.True(HostsEditor.BlockSurvived(path));
    }

    /// <summary>Ровно то, что видит программа после вмешательства защитника.</summary>
    [Fact]
    public void AReplacedFileHasNoBlockOfOurs()
    {
        var path = Written(
            "#This file has been replaced with its default version by Kaspersky Lab because of possible infection",
            "127.0.0.1 localhost");

        Assert.False(HostsEditor.BlockSurvived(path));
    }
}
