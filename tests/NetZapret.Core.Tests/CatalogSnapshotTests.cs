using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Снимок каталога Zapret читается вместе со своим каталогом.
/// </summary>
/// <remarks>
/// Снимок — отдельный файл рядом с catalog.yaml: тот при обновлении
/// сохраняется, этот заменяется. Читаться они обязаны вместе, иначе
/// снятые записи не дойдут ни до окна пина, ни до автоподбора.
/// </remarks>
public sealed class CatalogSnapshotTests
{
    [Fact]
    public void SnapshotIsReadBesideOwnCatalogAndOwnGoesFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var own = Path.Combine(dir, "catalog.yaml");

            File.WriteAllText(own, """
                services:
                  - name: Своё
                    names: [chatgpt.com]
                    addresses: [1.2.3.4]
                """);

            OwnCatalog.WriteSnapshot(
                Path.Combine(dir, OwnCatalog.SnapshotFile),
                [
                    new OwnCatalogEntry
                    {
                        Name = "ChatGPT & Sora (OpenAI) — XBOX DNS",
                        Names = ["chatgpt.com", "api.openai.com"],
                        Addresses = ["87.228.47.204"],
                    },
                ],
                [new PinCandidate("87.228.47.195", PinSource.Pool, "XBOX DNS")],
                ["Снимок для теста.", "", "Вторая строка."]);

            var catalog = OwnCatalog.Load(own);

            Assert.Empty(catalog.Problems);
            Assert.Equal(["Своё", "ChatGPT & Sora (OpenAI) — XBOX DNS"], catalog.Services.Select(s => s.Name));

            Assert.Equal(
                ["1.2.3.4", "87.228.47.204"],
                catalog.PinCandidates("chatgpt.com").Select(c => c.Address));

            var pool = Assert.Single(catalog.Intermediaries);
            Assert.Equal(("87.228.47.195", PinSource.Pool, "XBOX DNS"), (pool.Address, pool.Source, pool.Label));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Без снимка каталог работает как прежде.</summary>
    [Fact]
    public void NoSnapshotIsFine()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var own = Path.Combine(dir, "catalog.yaml");
            File.WriteAllText(own, "services: []\n");

            var catalog = OwnCatalog.Load(own);

            Assert.Empty(catalog.Problems);
            Assert.Empty(catalog.Intermediaries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Снимок, поставляемый с программой, разбирается без ошибок —
    /// он собран машиной, и опечатка в нём значила бы ошибку сборщика.
    /// </summary>
    [Fact]
    public void ShippedSnapshotParses()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, "config", OwnCatalog.SnapshotFile);

            if (!File.Exists(path))
                continue;

            var catalog = OwnCatalog.Load(path);

            Assert.Empty(catalog.Problems);
            Assert.NotEmpty(catalog.Services);
            Assert.All(catalog.Services, s => Assert.NotEmpty(s.Addresses));
            return;
        }
    }
}
