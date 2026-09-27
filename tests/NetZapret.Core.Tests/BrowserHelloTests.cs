using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Приветствие TLS, собранное по образцу браузера.
/// </summary>
/// <remarks>
/// Проверяется байтами, потому что байтами оно и написано. Ошибка в длине
/// блока не роняет ничего у нас — её обнаруживает сторона, отвечая отказом,
/// и отказ этот неотличим от того самого, ради различения которого
/// приветствие и собрано. То есть сломанный сборщик не упал бы, а тихо
/// подтверждал бы любую догадку.
/// </remarks>
public sealed class BrowserHelloTests
{
    private static byte[] Hello() => BrowserHello.Build("example.com");

    private static ushort At(byte[] bytes, int index) =>
        (ushort)((bytes[index] << 8) | bytes[index + 1]);

    /// <summary>Снаружи это запись рукопожатия и ничто иное.</summary>
    [Fact]
    public void It_is_a_handshake_record()
    {
        var hello = Hello();

        Assert.Equal(0x16, hello[0]);

        // Версия записи — 1.0, как у браузеров: старшая ломает часть
        // промежуточных узлов, а настоящая договаривается расширением.
        Assert.Equal(0x0301, At(hello, 1));

        // ClientHello.
        Assert.Equal(0x01, hello[5]);
    }

    /// <summary>
    /// Длины совпадают с тем, что за ними лежит.
    /// </summary>
    /// <remarks>
    /// Главная проверка файла. Длина записи, длина рукопожатия и длины
    /// вложенных блоков считаются вручную, и разъехаться им ничего
    /// не мешает — кроме этого теста.
    /// </remarks>
    [Fact]
    public void Every_length_matches_what_follows()
    {
        var hello = Hello();

        Assert.Equal(hello.Length - 5, At(hello, 3));

        int handshake = (hello[6] << 16) | (hello[7] << 8) | hello[8];
        Assert.Equal(hello.Length - 9, handshake);

        // Дальше по телу: версия (2), случайное (32), идентификатор сессии.
        int at = 9 + 2 + 32;
        Assert.Equal(32, hello[at]);
        at += 1 + 32;

        int ciphers = At(hello, at);
        at += 2 + ciphers;

        // Способы сжатия: один, и он «никакого».
        Assert.Equal(0x01, hello[at]);
        Assert.Equal(0x00, hello[at + 1]);
        at += 2;

        int extensions = At(hello, at);

        // Расширения кончаются ровно там, где кончается приветствие.
        Assert.Equal(hello.Length, at + 2 + extensions);
    }

    /// <summary>Каждое расширение укладывается в объявленную длину.</summary>
    /// <remarks>
    /// Проход по цепочке до самого конца: съехавшая длина одного расширения
    /// увела бы разбор в середину следующего, и обход не сошёлся бы с концом.
    /// </remarks>
    [Fact]
    public void The_extensions_form_an_unbroken_chain()
    {
        var hello = Hello();
        int at = Extensions(hello, out int end);
        var seen = new List<ushort>();

        while (at < end)
        {
            seen.Add(At(hello, at));
            at += 4 + At(hello, at + 2);
        }

        Assert.Equal(end, at);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    /// <summary>Имя уезжает в SNI, и уезжает настоящее.</summary>
    /// <remarks>
    /// Единственное, что в этом приветствии по-настоящему наше. Всё
    /// остальное здесь ради вида; имя — ради дела, и без него сторона
    /// ответит не за тот сайт.
    /// </remarks>
    [Fact]
    public void The_name_is_in_the_hello()
    {
        var hello = BrowserHello.Build("speedtest.net");

        Assert.Contains((ushort)0x0000, Types(hello));
        Assert.Contains(
            "speedtest.net",
            Encoding.ASCII.GetString(hello).Replace('\0', '.'));
    }

    /// <summary>
    /// То, чего у SChannel нет, — здесь есть.
    /// </summary>
    /// <remarks>
    /// Перечислены различия, ради которых всё и затеяно. Пропади любое —
    /// приветствие снова станет узнаваемым, а проверка перестанет отличать
    /// «нас не приняли» от «сюда не пускают», ничем этого не показав.
    /// </remarks>
    [Theory]
    [InlineData(0x0010, "ALPN")]
    [InlineData(0x0005, "status_request")]
    [InlineData(0x001B, "compress_certificate")]
    [InlineData(0x44CD, "application_settings")]
    [InlineData(0xFE0D, "encrypted_client_hello")]
    [InlineData(0x0033, "key_share")]
    [InlineData(0x002B, "supported_versions")]
    public void The_extensions_schannel_lacks_are_present(int type, string name)
    {
        Assert.True(Types(Hello()).Contains((ushort)type), name);
    }

    /// <summary>GREASE есть, и не в одном месте.</summary>
    /// <remarks>
    /// Приветствие без единого значения-пустышки браузером в 2026 году
    /// не бывает: их шлют и в шифрах, и в расширениях, и в группах.
    /// Отсутствие примечательно ровно так же, как присутствие.
    /// </remarks>
    [Fact]
    public void Grease_is_everywhere_a_browser_puts_it()
    {
        var hello = Hello();

        Assert.True(IsGrease(At(hello, Ciphers(hello))), "первый шифр");
        Assert.True(IsGrease(Types(hello)[0]), "первое расширение");
        Assert.Contains(Types(hello), IsGrease);
    }

    /// <summary>Три шифра TLS 1.3, а не два.</summary>
    /// <remarks>
    /// Отсутствие <c>chacha20-poly1305</c> (0x1303) — самая заметная примета
    /// SChannel: его нет ни у одного браузера, зато он есть у всякого
    /// телефона.
    /// </remarks>
    [Fact]
    public void All_three_modern_ciphers_are_offered()
    {
        var hello = Hello();
        int at = Ciphers(hello);
        int count = At(hello, at - 2) / 2;

        var suites = new List<ushort>();

        for (int i = 0; i < count; i++)
            suites.Add(At(hello, at + (i * 2)));

        Assert.Contains((ushort)0x1301, suites);
        Assert.Contains((ushort)0x1302, suites);
        Assert.Contains((ushort)0x1303, suites);
    }

    /// <summary>
    /// Предложен гибрид X25519MLKEM768, и ключ его проходит проверку модуля.
    /// </summary>
    /// <remarks>
    /// Без гибрида отпечаток устаревший (Chrome до 131). Ключ с коэффициентом
    /// от 3329 и выше сторона отвергает (FIPS 203) — отказом, который проба
    /// приняла бы за блокировку.
    /// </remarks>
    [Fact]
    public void Post_quantum_key_is_offered_and_well_formed()
    {
        Assert.Contains((ushort)0x11EC, Groups(Hello()));

        var key = BrowserHello.MlKemKey();
        Assert.Equal(1184, key.Length);

        for (int i = 0; i < 1152; i += 3)
        {
            int a = key[i] | ((key[i + 1] & 0x0F) << 8);
            int b = (key[i + 1] >> 4) | (key[i + 2] << 4);

            Assert.True(a < 3329 && b < 3329, $"коэффициент вне модуля на байте {i}");
        }
    }

    /// <summary>Отпечаток совпадает с настоящим Chromium 152.</summary>
    /// <remarks>
    /// Снят 27.09.2026 с tls.peet.ws во встроенном браузере Claude
    /// (Chrome/152.0.7977.130). Прежний состав давал <c>…_e5627efa2ab1</c>
    /// (Chrome ~110), и github.com, i.scdn.co, web.telegram.org отвечали
    /// на него illegal_parameter — проба видела отказ там, где браузер
    /// проходит. Разойдись отпечаток снова — проба опять заговорит
    /// не браузерным голосом, и заметит это только этот тест.
    /// </remarks>
    [Fact]
    public void Fingerprint_is_chromium_152()
    {
        Assert.Equal("t13d1516h2_8daaf6152771_806a8c22fdea", Ja4(Hello()));
        Assert.True(IsGrease(Types(Hello())[^1]), "последнее расширение");
    }

    /// <summary>Без добивки: Chrome добивает только короткие, а это под две тысячи байт.</summary>
    [Fact]
    public void It_is_not_padded_like_a_current_browsers()
    {
        Assert.DoesNotContain((ushort)0x0015, Types(Hello()));
        Assert.True(Hello().Length > 1500, $"всего {Hello().Length} Б");
    }

    /// <summary>Два приветствия подряд не совпадают.</summary>
    /// <remarks>
    /// Случайное, идентификатор сессии, ключ и выбор GREASE — всё берётся
    /// заново. Повторяющееся приветствие само по себе отпечаток.
    /// </remarks>
    [Fact]
    public void No_two_hellos_are_the_same()
    {
        Assert.NotEqual(Hello(), Hello());
    }

    /// <summary>Отказ читается по коду, а не показывается числом.</summary>
    /// <remarks>
    /// Коды, встречающиеся на деле, названы словами: разница между
    /// «состав не принят» и «сайт не знает такого имени» — это разница между
    /// «чинить нечего» и «мы пришли не туда». Редкие остаются числом,
    /// по которому их только и можно найти.
    /// </remarks>
    [Theory]
    [InlineData(40, "handshake_failure")]
    [InlineData(70, "protocol_version")]
    [InlineData(112, "unrecognized_name")]
    public void Known_alerts_are_named(byte code, string expected)
    {
        Assert.Contains(expected, BrowserHello.Alert(code));
    }

    [Fact]
    public void An_unknown_alert_keeps_its_number()
    {
        Assert.Contains("77", BrowserHello.Alert(77));
    }

    /// <summary>ServerHello — это принято.</summary>
    [Fact]
    public async Task A_server_hello_means_accepted()
    {
        // Запись рукопожатия, а внутри сообщение 0x02.
        var answer = await Answer([0x16, 0x03, 0x03, 0x00, 0x2A, 0x02, 0x00, 0x00, 0x26]);

        Assert.Equal(HelloAnswer.Accepted, answer.Answer);
    }

    /// <summary>Alert — это отказ, и отказ словами.</summary>
    /// <remarks>
    /// Отличать его от обрыва обязательно: ответ, пришедший словами, доказывает,
    /// что до стороны дошло и обратно вернулось. Вмешательство так не отвечает.
    /// </remarks>
    [Fact]
    public async Task An_alert_means_refused()
    {
        var answer = await Answer([0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 40]);

        Assert.Equal(HelloAnswer.Refused, answer.Answer);
        Assert.Contains("handshake_failure", answer.Detail);
    }

    /// <summary>Закрыли, не сказав ничего.</summary>
    [Fact]
    public async Task A_silent_close_is_silence()
    {
        Assert.Equal(HelloAnswer.Silence, (await Answer([])).Answer);
    }

    /// <summary>Оборвали на середине заголовка записи.</summary>
    [Fact]
    public async Task A_close_mid_header_is_a_reset()
    {
        Assert.Equal(HelloAnswer.Reset, (await Answer([0x16, 0x03])).Answer);
    }

    /// <summary>Ответ не на том языке — тоже не ServerHello.</summary>
    /// <remarks>
    /// Так отвечает подставленная страница-заглушка: на месте записи
    /// рукопожатия оказывается обычный текст.
    /// </remarks>
    [Fact]
    public async Task A_plain_text_answer_is_not_a_hello()
    {
        var answer = await Answer(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\n\r\n"));

        Assert.Equal(HelloAnswer.Reset, answer.Answer);
        Assert.Contains("не похож на TLS", answer.Detail);
    }

    private static async Task<HelloResult> Answer(byte[] bytes) =>
        await BrowserHello.ReadAnswerAsync(
            new MemoryStream(bytes),
            System.Diagnostics.Stopwatch.StartNew(),
            CancellationToken.None);

    private static bool IsGrease(ushort value) =>
        (value & 0x0F0F) == 0x0A0A && (value >> 8) == (value & 0xFF);

    /// <summary>Смещение первого шифра.</summary>
    private static int Ciphers(byte[] hello) => 9 + 2 + 32 + 1 + 32 + 2;

    /// <summary>Смещение первого расширения и конец их списка.</summary>
    private static int Extensions(byte[] hello, out int end)
    {
        int at = Ciphers(hello) - 2;
        at += 2 + At(hello, at);
        at += 2;

        int length = At(hello, at);
        end = at + 2 + length;

        return at + 2;
    }

    /// <summary>Тело расширения по типу.</summary>
    private static byte[] Body(byte[] hello, ushort type)
    {
        int at = Extensions(hello, out int end);

        while (at < end)
        {
            int length = At(hello, at + 2);

            if (At(hello, at) == type)
                return hello[(at + 4)..(at + 4 + length)];

            at += 4 + length;
        }

        return [];
    }

    /// <summary>Предложенные группы обмена ключами.</summary>
    private static IReadOnlyList<ushort> Groups(byte[] hello)
    {
        var body = Body(hello, 0x000A);
        var groups = new List<ushort>();

        for (int i = 2; i < body.Length; i += 2)
            groups.Add(At(body, i));

        return groups;
    }

    /// <summary>
    /// JA4 приветствия: шифры и расширения без GREASE, отсортированные,
    /// подписи — в порядке приветствия. Как считает FoxIO.
    /// </summary>
    private static string Ja4(byte[] hello)
    {
        static string Hash(string text) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(text)))
                .ToLowerInvariant()[..12];

        int at = Ciphers(hello);
        int count = At(hello, at - 2) / 2;

        var ciphers = Enumerable.Range(0, count)
            .Select(i => At(hello, at + (i * 2)))
            .Where(c => !IsGrease(c))
            .Select(c => c.ToString("x4"))
            .Order(StringComparer.Ordinal)
            .ToList();

        var types = Types(hello).Where(t => !IsGrease(t)).ToList();

        var sorted = types
            .Where(t => t is not 0x0000 and not 0x0010)
            .Select(t => t.ToString("x4"))
            .Order(StringComparer.Ordinal);

        var signatures = Body(hello, 0x000D);
        var algorithms = Enumerable.Range(0, At(signatures, 0) / 2)
            .Select(i => At(signatures, 2 + (i * 2)).ToString("x4"));

        return $"t13d{ciphers.Count:D2}{types.Count:D2}h2"
            + $"_{Hash(string.Join(",", ciphers))}"
            + $"_{Hash(string.Join(",", sorted) + "_" + string.Join(",", algorithms))}";
    }

    /// <summary>Типы расширений по порядку.</summary>
    private static IReadOnlyList<ushort> Types(byte[] hello)
    {
        int at = Extensions(hello, out int end);
        var types = new List<ushort>();

        while (at < end)
        {
            types.Add(At(hello, at));
            at += 4 + At(hello, at + 2);
        }

        return types;
    }
}
