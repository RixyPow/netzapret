using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Счётчик скачиваний: сумма по всем вложениям всех выпусков страницы.
/// </summary>
public sealed class DownloadStatsTests
{
    [Fact]
    public void DownloadsAreSummedAcrossReleasesAndAssets()
    {
        var (sum, releases) = DownloadStats.Sum("""
            [
              { "tag_name": "v0.8.1", "assets": [ { "name": "NetZapret.zip", "download_count": 7 } ] },
              { "tag_name": "v0.8.0", "assets": [ { "name": "NetZapret.zip", "download_count": 26 },
                                                  { "name": "notes.txt", "download_count": 2 } ] },
              { "tag_name": "v0.1.0", "assets": [] },
              { "tag_name": "v0.0.1" }
            ]
            """);

        Assert.Equal(35, sum);
        Assert.Equal(4, releases);
    }
}
