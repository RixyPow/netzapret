using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверяет запись пинов в hosts.
/// </summary>
/// <remarks>
/// Файл системный и общий: его ведут и Zapret GUI, и руками, и чужие наборы.
/// Единственная ошибка здесь стоит человеку работающей сети — у этого
/// пользователя чистка файла разом выбросила его из всех сервисов сразу.
/// Поэтому проверяется не только то, что мы пишем, но и то, чего не трогаем.
/// </remarks>
public class HostsPinTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hosts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private void Write(params string[] lines) => File.WriteAllLines(_path, lines);

    private string[] Read() => File.ReadAllLines(_path);

    [Fact]
    public void A_pin_is_written_into_our_own_block()
    {
        Write("# заголовок", string.Empty);

        var result = HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);

        Assert.Equal(1, result.Pinned);
        Assert.Contains(Read(), l => l.Contains(HostsEditor.BlockBegin));
        Assert.Contains(Read(), l => l.Trim() == "1.2.3.4 chatgpt.com");
        Assert.Contains(Read(), l => l.Contains(HostsEditor.BlockEnd));
    }

    /// <summary>Чужие строки переживают запись в неприкосновенности.</summary>
    /// <remarks>
    /// Главное правило файла. Стереть строку Zapret GUI значит сломать
    /// то, что человек настраивал не у нас и чинить будет не у нас.
    /// </remarks>
    [Fact]
    public void Foreign_lines_survive_untouched()
    {
        Write(
            "# заголовок",
            "# >>> zapretgui:hosts managed begin >>>",
            "9.9.9.9 spotify.com",
            "# <<< zapretgui:hosts managed end <<<",
            "8.8.8.8 example.org");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);

        var lines = Read();

        Assert.Contains(lines, l => l.Trim() == "9.9.9.9 spotify.com");
        Assert.Contains(lines, l => l.Trim() == "8.8.8.8 example.org");
        Assert.Contains(lines, l => l.Contains("zapretgui:hosts managed begin"));
    }

    /// <summary>Наш блок встаёт выше чужих записей.</summary>
    /// <remarks>
    /// При двух записях на одно имя разбор идёт сверху. Блок, дописанный
    /// в конец, проиграл бы чужому пину молча — и человек решил бы,
    /// что закрепление не сработало.
    /// </remarks>
    [Fact]
    public void Our_block_goes_above_existing_entries()
    {
        Write("# заголовок", "5.5.5.5 chatgpt.com");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);

        var lines = Read();
        int ours = Array.FindIndex(lines, l => l.Trim() == "1.2.3.4 chatgpt.com");
        int theirs = Array.FindIndex(lines, l => l.Trim() == "5.5.5.5 chatgpt.com");

        Assert.True(ours >= 0 && theirs >= 0);
        Assert.True(ours < theirs, "наш пин должен разбираться раньше чужого");
    }

    /// <summary>Шапка из комментариев остаётся сверху.</summary>
    [Fact]
    public void The_comment_header_stays_first()
    {
        Write("# Copyright Microsoft", "#", "# пример", "8.8.8.8 example.org");

        HostsEditor.Pin(new Dictionary<string, string> { ["a.test"] = "1.1.1.1" }, _path);

        var lines = Read();

        Assert.StartsWith("# Copyright", lines[0]);
        Assert.True(Array.FindIndex(lines, l => l.Contains(HostsEditor.BlockBegin)) >= 3);
    }

    /// <summary>О чужой записи на то же имя сообщается.</summary>
    /// <remarks>
    /// Молчать нельзя: снимут наш пин — вернётся чужой, и снятие будет
    /// выглядеть несработавшим.
    /// </remarks>
    [Fact]
    public void A_competing_foreign_line_is_reported()
    {
        Write("# заголовок", "5.5.5.5 chatgpt.com", "8.8.8.8 example.org");

        var result = HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);

        Assert.Single(result.Shadowed);
        Assert.Contains("5.5.5.5", result.Shadowed[0]);
    }

    [Fact]
    public void Nothing_is_reported_when_no_one_competes()
    {
        Write("# заголовок", "8.8.8.8 example.org");

        var result = HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);

        Assert.Empty(result.Shadowed);
    }

    /// <summary>Второе закрепление не смывает первое.</summary>
    /// <remarks>
    /// Закрепляют по одному сервису. Переписывать блок целиком значило бы
    /// снимать все прежние пины при каждом новом.
    /// </remarks>
    [Fact]
    public void Pinning_a_second_service_keeps_the_first()
    {
        Write("# заголовок");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);
        var result = HostsEditor.Pin(new Dictionary<string, string> { ["spotify.com"] = "5.6.7.8" }, _path);

        Assert.Equal(2, result.Pinned);
        Assert.Contains(Read(), l => l.Trim() == "1.2.3.4 chatgpt.com");
        Assert.Contains(Read(), l => l.Trim() == "5.6.7.8 spotify.com");
    }

    [Fact]
    public void Pinning_the_same_name_again_replaces_the_address()
    {
        Write("# заголовок");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);
        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "9.9.9.9" }, _path);

        Assert.Contains(Read(), l => l.Trim() == "9.9.9.9 chatgpt.com");
        Assert.DoesNotContain(Read(), l => l.Trim() == "1.2.3.4 chatgpt.com");
        Assert.Single(HostsEditor.Pins(_path));
    }

    [Fact]
    public void Unpinning_removes_only_the_named_ones()
    {
        Write("# заголовок");

        HostsEditor.Pin(
            new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4", ["spotify.com"] = "5.6.7.8" },
            _path);

        var result = HostsEditor.Unpin(["chatgpt.com"], _path);

        Assert.Equal(1, result.Pinned);
        Assert.DoesNotContain(Read(), l => l.Trim() == "1.2.3.4 chatgpt.com");
        Assert.Contains(Read(), l => l.Trim() == "5.6.7.8 spotify.com");
    }

    /// <summary>Опустевший блок исчезает целиком.</summary>
    /// <remarks>Иначе в файле копятся наши следы от сервисов, которых нет.</remarks>
    [Fact]
    public void An_emptied_block_disappears()
    {
        Write("# заголовок", "8.8.8.8 example.org");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);
        HostsEditor.Unpin(["chatgpt.com"], _path);

        Assert.DoesNotContain(Read(), l => l.Contains(HostsEditor.BlockBegin));
        Assert.Contains(Read(), l => l.Trim() == "8.8.8.8 example.org");
    }

    /// <summary>Снятие не трогает чужую строку на то же имя.</summary>
    [Fact]
    public void Unpinning_leaves_the_foreign_line_alone()
    {
        Write("# заголовок", "5.5.5.5 chatgpt.com");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);
        HostsEditor.Unpin(["chatgpt.com"], _path);

        Assert.Contains(Read(), l => l.Trim() == "5.5.5.5 chatgpt.com");
    }

    [Fact]
    public void Pins_lists_what_we_put_there_and_nothing_else()
    {
        Write("# заголовок", "5.5.5.5 example.org");

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "1.2.3.4" }, _path);

        var pins = HostsEditor.Pins(_path);

        Assert.Single(pins);
        Assert.Equal("1.2.3.4", pins["chatgpt.com"]);
    }

    [Fact]
    public void Pins_are_empty_when_we_never_wrote()
    {
        Write("# заголовок", "5.5.5.5 example.org");

        Assert.Empty(HostsEditor.Pins(_path));
    }

    /// <summary>Перед всякой записью остаётся копия.</summary>
    [Fact]
    public void A_backup_is_made_before_writing()
    {
        Write("# заголовок", "8.8.8.8 example.org");

        var result = HostsEditor.Pin(new Dictionary<string, string> { ["a.test"] = "1.1.1.1" }, _path);

        Assert.NotNull(result.Backup);
        Assert.True(File.Exists(result.Backup));
        Assert.Contains(File.ReadAllLines(result.Backup!), l => l.Trim() == "8.8.8.8 example.org");
    }

    /// <summary>Сбор забирает записи к нам и убирает повторы.</summary>
    /// <remarks>
    /// В живом файле оказалось 839 записей на 796 имён — сорок три повтора,
    /// накопленных тремя механизмами сразу. Действует из повторов первый,
    /// и какой именно, по файлу не видно.
    /// </remarks>
    [Fact]
    public void Absorb_gathers_entries_and_drops_repeats()
    {
        Write("# заголовок", "1.1.1.1 a.test", "2.2.2.2 b.test", "1.1.1.1 a.test");

        var result = HostsEditor.Absorb(_path);

        Assert.Equal(2, result.Pinned);
        Assert.Single(Read(), l => l.Trim() == "1.1.1.1 a.test");
        Assert.Single(Read(), l => l.Trim() == "2.2.2.2 b.test");
    }

    /// <summary>Все адреса имени сохраняются, а не первый попавшийся.</summary>
    /// <remarks>
    /// У <c>instagram.com</c> их три, включая IPv6, у <c>openai.com</c>
    /// четыре, и Windows перебирает их по очереди. Оставить один значило бы
    /// урезать запасные пути своими руками.
    /// </remarks>
    [Fact]
    public void Absorb_keeps_every_address_of_a_name()
    {
        Write(
            "# заголовок",
            "163.70.151.174 instagram.com",
            "2a03:2880:f330:25:face:b00c:0:4420 instagram.com",
            "2a03:2880:f342:22:face:b00c:0:4420 instagram.com");

        HostsEditor.Absorb(_path);

        var pins = Read().Where(l => l.Contains("instagram.com")).ToList();

        Assert.Equal(3, pins.Count);
        Assert.Contains(pins, l => l.Trim().StartsWith("163.70.151.174"));
        Assert.Contains(pins, l => l.Trim().StartsWith("2a03:2880:f330"));
        Assert.Contains(pins, l => l.Trim().StartsWith("2a03:2880:f342"));
    }

    /// <summary>Выключенные строки сбор не трогает.</summary>
    /// <remarks>
    /// Их выключили намеренно. Втянуть такую строку в наш блок — значит либо
    /// потерять её, либо включить обратно; и то и другое сделает за человека
    /// выбор, который он уже сделал сам.
    /// </remarks>
    [Fact]
    public void Absorb_leaves_disabled_lines_alone()
    {
        Write("# заголовок", "1.1.1.1 a.test", "# 9.9.9.9 off.test");

        HostsEditor.Absorb(_path);

        Assert.Contains(Read(), l => l.Trim() == "# 9.9.9.9 off.test");
        Assert.DoesNotContain(HostsEditor.Pins(_path).Keys, k => k == "off.test");
    }

    /// <summary>Собранное не остаётся на прежнем месте.</summary>
    /// <remarks>Иначе повторы не исчезли бы, а удвоились.</remarks>
    [Fact]
    public void Absorb_removes_the_originals()
    {
        Write("# заголовок", "1.1.1.1 a.test", "2.2.2.2 b.test");

        HostsEditor.Absorb(_path);

        var lines = Read();
        var block = Array.FindIndex(lines, l => l.Contains(HostsEditor.BlockEnd));

        Assert.All(lines.Skip(block), l => Assert.Null(l.Trim() == "1.1.1.1 a.test" ? "дубль" : null));
        Assert.Single(lines, l => l.Trim() == "1.1.1.1 a.test");
    }

    /// <summary>Уже собранное переживает повторный сбор.</summary>
    [Fact]
    public void Absorb_is_repeatable()
    {
        Write("# заголовок", "1.1.1.1 a.test");

        HostsEditor.Absorb(_path);
        var result = HostsEditor.Absorb(_path);

        Assert.Equal(1, result.Pinned);
        Assert.Single(Read(), l => l.Trim() == "1.1.1.1 a.test");
    }

    /// <summary>Ведущие точки и звёздочки списка в файл не попадают.</summary>
    /// <remarks>
    /// Списки Zapret пишут зоны как <c>*.chatgpt.com</c>, а hosts понимает
    /// только точные имена: строка со звёздочкой не совпала бы ни с чем
    /// и молча ничего не делала.
    /// </remarks>
    [Fact]
    public void Wildcards_are_trimmed_to_plain_names()
    {
        Write("# заголовок");

        HostsEditor.Pin(new Dictionary<string, string> { ["*.chatgpt.com"] = "1.2.3.4" }, _path);

        Assert.Contains(Read(), l => l.Trim() == "1.2.3.4 chatgpt.com");
    }
}
