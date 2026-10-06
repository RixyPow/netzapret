using NetZapret.Core;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Программа в облачной папке — оговорка «Диагностики».
/// </summary>
/// <remarks>
/// Отчёт друга владельца 06.10: <c>%USERPROFILE%\OneDrive\Рабочий стол\NetZapret</c>.
/// Владелец 07.10: «делай».
/// </remarks>
public sealed class CloudFolderTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void OneDriveIsFoundByItsVariable()
    {
        var env = Env(("OneDrive", @"C:\Users\friend\OneDrive"));

        Assert.Equal("OneDrive", CloudFolder.Of(@"C:\Users\friend\OneDrive\Рабочий стол\NetZapret", env));
        Assert.Equal("OneDrive", CloudFolder.Of(@"c:\users\friend\onedrive\netzapret\", env));
        Assert.Null(CloudFolder.Of(@"C:\Users\friend\OneDriveBackup\NetZapret", Env(("OneDrive", @"C:\Users\friend\OneDrive2"))));
    }

    [Fact]
    public void OneDriveIsFoundByTheFolderNameWithoutVariables()
    {
        Assert.Equal("OneDrive", CloudFolder.Of(@"D:\OneDrive\NetZapret", Env()));
        Assert.Equal("OneDrive", CloudFolder.Of(@"C:\Users\u\OneDrive - Контора\Desktop\NetZapret", Env()));
    }

    [Theory]
    [InlineData(@"C:\Users\u\Dropbox\NetZapret", "Dropbox")]
    [InlineData(@"G:\My Drive\NetZapret", "Google Диск")]
    [InlineData(@"C:\Users\u\YandexDisk\NetZapret", "Яндекс Диск")]
    public void OtherClientsAreFoundByFolder(string path, string cloud) =>
        Assert.Equal(cloud, CloudFolder.Of(path, Env()));

    [Fact]
    public void AnOrdinaryFolderIsNotCloud()
    {
        Assert.Null(CloudFolder.Of(@"C:\NetZapret", Env(("OneDrive", @"C:\Users\u\OneDrive"))));
        Assert.Null(CloudFolder.Of(@"C:\Users\u\Desktop\NetZapret", Env()));
    }

    [Fact]
    public void DoctorWarnsAndSaysWhatToDo()
    {
        var line = Assert.Single(Doctor.Place(@"C:\Users\u\OneDrive\Рабочий стол\NetZapret", Env(("OneDrive", @"C:\Users\u\OneDrive"))));

        Assert.Equal(DoctorLevel.Warn, line.Level);
        Assert.Contains("OneDrive", line.Text);
        Assert.Contains(@"C:\NetZapret", line.Text);
        Assert.Contains("автозапуск", line.Text);

        Assert.Equal(DoctorLevel.Ok, Assert.Single(Doctor.Place(@"C:\NetZapret", Env())).Level);
    }
}
