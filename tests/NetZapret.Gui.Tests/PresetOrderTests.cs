using System.IO;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Свой порядок пресетов: сохранение, чтение и раскладка.
/// </summary>
/// <remarks>
/// Заведено по требованию владельца: «наши сверху, чужие ниже». Раскладка
/// ведётся по именам файлов, а порядок хранится отдельным файлом, который
/// человек может и открыть, и испортить.
/// </remarks>
public sealed class PresetOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"netzapret-order-{Guid.NewGuid():N}");

    private readonly string _was = Directory.GetCurrentDirectory();

    public PresetOrderTests()
    {
        Directory.CreateDirectory(_root);

        // Apply читает порядок по пути по умолчанию, то есть от текущего
        // каталога. Так же делает и окно.
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_was);

        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void Nothing_saved_means_the_order_stays_as_it_was()
    {
        string[] items = ["в", "а", "б"];

        Assert.Equal(items, PresetOrder.Apply(items, s => s));
    }

    [Fact]
    public void A_saved_order_is_applied()
    {
        PresetOrder.Save(["б", "а"]);

        Assert.Equal(["б", "а", "в"], PresetOrder.Apply(["а", "б", "в"], s => s));
    }

    /// <summary>
    /// Незнакомые уходят в конец, а не пропадают.
    /// </summary>
    /// <remarks>
    /// Пресет, положенный в папку после того, как порядок был задан, должен
    /// появиться в списке. Пропасть он не имеет права: человек скопировал
    /// файл и не находит его в программе — это выглядит как поломка чтения,
    /// а не как особенность сортировки.
    /// </remarks>
    [Fact]
    public void Unknown_names_go_to_the_end_keeping_their_own_order()
    {
        PresetOrder.Save(["б"]);

        Assert.Equal(["б", "я", "а"], PresetOrder.Apply(["я", "б", "а"], s => s));
    }

    /// <summary>
    /// Повторы в сохранённом порядке переживаются молча.
    /// </summary>
    /// <remarks>
    /// Прежде роняли весь раздел: три пресета Zapret объявляют себя
    /// «Universal V5», порядок сохранялся по этому имени, и список пропадал
    /// с жалобой на повторный ключ.
    /// </remarks>
    [Fact]
    public void A_duplicated_name_does_not_break_the_list()
    {
        PresetOrder.Save(["Universal V5", "Universal V5", "а"]);

        var laid = PresetOrder.Apply(["а", "Universal V5"], s => s);

        Assert.Equal(["Universal V5", "а"], laid);
    }

    /// <summary>Испорченный файл означает порядок по умолчанию, а не падение.</summary>
    /// <remarks>
    /// Файл лежит в <c>config\</c> рядом с остальными и открывается руками.
    /// Ронять из-за него раздел значило бы наказывать за любопытство.
    /// </remarks>
    [Fact]
    public void A_broken_file_means_no_order_at_all()
    {
        Directory.CreateDirectory("config");
        File.WriteAllText(PresetOrder.DefaultPath, "{ это не список }");

        Assert.Empty(PresetOrder.Load());
        Assert.Equal(["я", "а"], PresetOrder.Apply(["я", "а"], s => s));
    }

    /// <summary>Сохранённое читается обратно тем же.</summary>
    [Fact]
    public void What_was_saved_is_what_is_read()
    {
        // С кириллицей нарочно: имена пресетов на ней и написаны, а JSON
        // по умолчанию их экранирует в \uXXXX — файл при этом читается
        // программой, но не человеком, а открывать его ему и предлагают.
        PresetOrder.Save(["Универсальный", "Свой"]);

        Assert.Equal(["Универсальный", "Свой"], PresetOrder.Load());
        Assert.Contains("Универсальный", File.ReadAllText(PresetOrder.DefaultPath));
    }
}
