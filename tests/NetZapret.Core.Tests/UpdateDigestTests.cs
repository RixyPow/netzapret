using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Архив обновления сверяется с хэшем, который GitHub посчитал сам (поле digest).
/// </summary>
/// <remarks>
/// До 29.10 сверялся один размер, а распаковка идёт в папку под исключением
/// Defender. Вики zapret.moe (раздел «virus», август 2026): поддельные сборки
/// обходов ловятся контрольной суммой, только если её сверять.
/// </remarks>
public sealed class UpdateDigestTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"nz-digest-{Guid.NewGuid():N}.zip");

    public void Dispose()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }

    [Fact]
    public void Digest_from_github_becomes_lowercase_hex()
    {
        var all = UpdateCheck.FromReleases("""
            [ { "tag_name": "v1.2.2", "draft": false, "prerelease": false, "body": "",
                "assets": [ { "name": "NetZapret-1.2.2.zip", "browser_download_url": "https://example/a.zip", "size": 3,
                              "digest": "sha256:C442BE596ABB2C135EF1AE6F2C562427FEEE59E14B86C0CFB0D8F2E3D3A0D718" } ] } ]
            """);

        Assert.Equal("c442be596abb2c135ef1ae6f2c562427feee59e14b86c0cfb0d8f2e3d3a0d718", all[0].ArchiveSha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha512:c442be596abb2c135ef1ae6f2c562427feee59e14b86c0cfb0d8f2e3d3a0d718")]
    [InlineData("sha256:c442be")]
    [InlineData("sha256:z442be596abb2c135ef1ae6f2c562427feee59e14b86c0cfb0d8f2e3d3a0d718")]
    public void Anything_else_means_no_digest_and_size_only(string? digest)
    {
        Assert.Null(UpdateCheck.Sha256(digest));
    }

    [Fact]
    public void Release_without_digest_keeps_working_as_before()
    {
        var all = UpdateCheck.FromReleases("""
            [ { "tag_name": "v0.13.1", "draft": false, "prerelease": false, "body": "",
                "assets": [ { "name": "NetZapret-0.13.1.zip", "browser_download_url": "https://example/a.zip", "size": 1 } ] } ]
            """);

        Assert.Null(all[0].ArchiveSha256);
    }

    [Fact]
    public void The_same_bytes_match_and_one_changed_byte_does_not()
    {
        File.WriteAllBytes(_file, "abc"u8.ToArray());
        const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        Assert.True(UpdateInstaller.Matches(_file, abc));
        Assert.True(UpdateInstaller.Matches(_file, abc.ToUpperInvariant()));

        File.WriteAllBytes(_file, "abd"u8.ToArray());
        Assert.False(UpdateInstaller.Matches(_file, abc));
    }
}
