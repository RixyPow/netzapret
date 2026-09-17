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
    [InlineData(0x4469, "application_settings")]
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

    /// <summary>Добито до пятисот двенадцати байт.</summary>
    /// <remarks>
    /// Не украшение: приветствия короче двухсот пятидесяти шести байт ломают
    /// часть промежуточных узлов, и браузеры добивают их с тех пор, как это
    /// выяснилось. Короткое приветствие выдало бы нас и здесь.
    /// </remarks>
    [Fact]
    public void It_is_padded_like_a_browsers()
    {
        Assert.True(Hello().Length >= 512, $"всего {Hello().Length} Б");
        Assert.Contains((ushort)0x0015, Types(Hello()));
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
