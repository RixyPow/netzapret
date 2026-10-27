using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Пометка проверки блокировок знает свои рецепты: winws2 ставит их раньше секций пресета (27.10).
/// </summary>
public sealed class OwnDesyncLookupTests
{
    [Fact]
    public void A_name_on_an_own_recipe_is_named_by_it_first_profile_wins()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"nz-own-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            var pass = Path.Combine(folder, "pass.txt");
            var pda = Path.Combine(folder, "seqovl-4pda.txt");
            File.WriteAllLines(pass, ["outlook.live.com"]);
            File.WriteAllLines(pda, ["firstlook.gg", "seriesgraph.com"]);

            var lookup = OwnDesyncLists.Lookup(
            [
                new OwnDesyncProfile { Name = "pass", Steps = ["pass"], HostListPath = pass },
                new OwnDesyncProfile { Name = "seqovl-4pda", Steps = ["multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda"], HostListPath = pda },
            ]);

            var series = lookup("www.seriesgraph.com");

            Assert.NotNull(series);
            Assert.Equal("seqovl-4pda", series!.Name);
            Assert.StartsWith("«seqovl-4pda» → multisplit", series.Describe());

            Assert.True(lookup("outlook.live.com")!.IsPassThrough);
            Assert.Null(lookup("example.com"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
