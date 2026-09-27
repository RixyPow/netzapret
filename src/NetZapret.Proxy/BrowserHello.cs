using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetZapret.Proxy;

/// <summary>Чем ответила сторона на приветствие, собранное нами.</summary>
public enum HelloAnswer
{
    /// <summary>Соединения не было — спрашивать было нечего.</summary>
    NoRoute,

    /// <summary>Пришёл ServerHello: приветствие принято.</summary>
    Accepted,

    /// <summary>Пришёл alert: сторона ответила словами и отказала.</summary>
    Refused,

    /// <summary>Соединение оборвано.</summary>
    Reset,

    /// <summary>Ответа не было вовсе.</summary>
    Silence,
}

/// <summary>Исход одного вопроса, заданного своим приветствием.</summary>
public readonly record struct HelloResult(HelloAnswer Answer, string Detail, TimeSpan Elapsed);

/// <summary>
/// Приветствие TLS, собранное по образцу браузера, а не выданное системой.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по замеру 16.09.2026: на <c>speedtest.net</c> и <c>i.scdn.co</c>
/// приветствие SChannel отвергалось, а собранное по образцу Chrome получало
/// ServerHello — по тому же маршруту, в ту же минуту. Значит отказ был
/// не блокировкой и не настройкой стороны, а тем, что нас узнавали
/// по приветствию.
/// </para>
/// <para>
/// Отличается наше от браузерного не мелочью. У SChannel нет GREASE вовсе,
/// два шифра TLS 1.3 из трёх (нет <c>chacha20-poly1305</c>), нет
/// <c>compress_certificate</c>, нет ALPS, нет <c>status_request</c>. Менять
/// это через <c>SslStream</c> нельзя: <c>CipherSuitesPolicy</c> на Windows
/// бросает <see cref="PlatformNotSupportedException"/>, а из всего состава
/// наружу вынесены только версии протокола и ALPN.
/// </para>
/// <para>
/// Поэтому приветствие здесь пишется байтами. Рукопожатие при этом
/// <b>не доводится до конца</b> — и не должно: вопрос ровно один, принимает
/// ли сторона такое приветствие. Ответ виден по первой же записи, а всё, что
/// дальше, потребовало бы своей реализации X25519, расписания ключей и
/// AES-GCM — тысяч строк криптографии ради вопроса, на который уже ответили.
/// </para>
/// <para>
/// Ключ в <c>key_share</c> по той же причине взят случайными байтами:
/// X25519 принимает любые тридцать два, сторона посчитает общий секрет
/// и пришлёт ServerHello, а расшифровывать его мы не станем. С ML-KEM так
/// нельзя: сторона проверяет, что каждый коэффициент ключа меньше 3329,
/// и случайные байты этой проверки почти никогда не проходят. Поэтому
/// коэффициенты тянутся случайно, но из допустимого диапазона
/// (<see cref="MlKemKey"/>) — секрета к ним у нас нет, и он не нужен.
/// </para>
/// </remarks>
public static class BrowserHello
{
    /// <summary>
    /// Значения-пустышки, которыми браузеры проверяют чужую терпимость
    /// к незнакомому.
    /// </summary>
    /// <remarks>
    /// RFC 8701. Смысл их в том, что они ничего не значат: сторона обязана
    /// пропустить незнакомый номер мимо, и тот, кто на них спотыкается,
    /// обнаруживает себя. Для нас важно другое — их отсутствие столь же
    /// приметно, как присутствие: приветствие без единого GREASE в 2026 году
    /// браузером не бывает.
    /// </remarks>
    private static readonly ushort[] Grease =
    [
        0x0A0A, 0x1A1A, 0x2A2A, 0x3A3A, 0x4A4A, 0x5A5A, 0x6A6A, 0x7A7A,
        0x8A8A, 0x9A9A, 0xAAAA, 0xBABA, 0xCACA, 0xDADA, 0xEAEA, 0xFAFA,
    ];

    /// <summary>Сколько ждать первой записи в ответ.</summary>
    /// <remarks>
    /// Короче общего сетевого срока: соединение уже установлено, и ServerHello
    /// приходит следом за нашим приветствием. Долгое ожидание здесь означало бы
    /// молчание, а молчание — это уже ответ.
    /// </remarks>
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Собирает приветствие для <paramref name="host"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Состав взят у Chromium 152, снят 27.09.2026 с tls.peet.ws: JA4
    /// <c>t13d1516h2_8daaf6152771_806a8c22fdea</c>. До того здесь был состав
    /// Chrome ~110 (<c>…_e5627efa2ab1</c>: старый ALPS 0x4469, добивка, ни ECH,
    /// ни постквантового обмена) — настоящий, но устаревший отпечаток.
    /// Вики Zapret GUI разбирает случай, когда ТСПУ режет ровно один
    /// устаревший отпечаток Chrome (<c>…_d8a2da3f94cd</c>, Chrome 134),
    /// пропуская свежие: проба с таким отпечатком приняла бы блокировку
    /// отпечатка за блокировку сайта.
    /// </para>
    /// <para>
    /// Порядок расширений Chrome с версии 110 перемешивает при каждом
    /// соединении, оставляя GREASE по краям, — так же и здесь. JA4 от порядка
    /// не зависит: он сортирует расширения.
    /// </para>
    /// </remarks>
    public static byte[] Build(string host)
    {
        var body = new List<byte>();

        // Версия в самом приветствии всегда 1.2, даже когда клиент умеет 1.3:
        // настоящая версия договаривается расширением supported_versions.
        // Так сделано ради промежуточных узлов, которые старшую версию
        // в этом поле не переживают.
        body.AddRange([0x03, 0x03]);

        body.AddRange(RandomNumberGenerator.GetBytes(32));

        // Непустой идентификатор сессии — примета совместимости с TLS 1.2:
        // браузеры шлют тридцать два случайных байта и в тех соединениях,
        // где возобновлять нечего.
        body.Add(32);
        body.AddRange(RandomNumberGenerator.GetBytes(32));

        body.AddRange(Block16(Ciphers()));

        // Сжатия нет — в TLS 1.3 оно запрещено, в 1.2 не используется никем.
        body.AddRange([0x01, 0x00]);

        body.AddRange(Block16(Extensions(host)));

        var handshake = new List<byte> { 0x01 };
        handshake.AddRange(Block24(body));

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.AddRange(Block16(handshake));

        return [.. record];
    }

    private static List<byte> Ciphers()
    {
        var list = new List<byte>();

        Add16(list, Pick());

        // Три шифра TLS 1.3, а не два. Отсутствие chacha20-poly1305 —
        // самая заметная примета SChannel: его нет ни у одного браузера,
        // зато он есть у всякого телефона, и сторона, считающая шифры,
        // видит разницу сразу.
        foreach (ushort suite in (ushort[])
            [0x1301, 0x1302, 0x1303,
             0xC02B, 0xC02F, 0xC02C, 0xC030, 0xCCA9, 0xCCA8,
             0xC013, 0xC014, 0x009C, 0x009D, 0x002F, 0x0035])
        {
            Add16(list, suite);
        }

        return list;
    }

    private static List<byte> Extensions(string host)
    {
        // Пустышек-расширений две — в начале и в конце, как у Chrome, — и они
        // обязаны различаться. Дважды одно и то же расширение в приветствии
        // протокол запрещает, и сторона вправе оборвать разговор: мы получили
        // бы отказ, неотличимый от того, ради различения которого всё
        // и затеяно. Выпадает такое раз на шестнадцать.
        var (opening, closing) = TwoGrease();

        // Имя — единственное, что в приветствии по-настоящему наше.
        // Всё остальное здесь ради вида; имя ради дела.
        var name = Encoding.ASCII.GetBytes(host);
        var sni = new List<byte> { 0x00 };
        sni.AddRange(Block16(name));

        // Одна пустышка на группы и на ключ: у Chrome они совпадают.
        ushort group = Pick();

        var groups = new List<byte>();
        Add16(groups, group);
        Add16(groups, 0x11EC);
        Add16(groups, 0x001D);
        Add16(groups, 0x0017);
        Add16(groups, 0x0018);

        var alpn = new List<byte>();
        Name(alpn, "h2");
        Name(alpn, "http/1.1");

        // ML-DSA (0x0904–0x0906) впереди — так у Chromium 152.
        var signatures = new List<byte>();

        foreach (ushort algorithm in (ushort[])
            [0x0904, 0x0905, 0x0906,
             0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601])
        {
            Add16(signatures, algorithm);
        }

        // Ключей три: пустышка, гибрид X25519MLKEM768 и запасной X25519.
        // У гибрида сперва ключ ML-KEM, потом X25519 — так в черновике IETF.
        var shares = new List<byte>();
        Add16(shares, group);
        shares.AddRange(Block16(new byte[] { 0x00 }));
        Add16(shares, 0x11EC);
        shares.AddRange(Block16([.. MlKemKey(), .. RandomNumberGenerator.GetBytes(32)]));
        Add16(shares, 0x001D);
        shares.AddRange(Block16(RandomNumberGenerator.GetBytes(32)));

        var versions = new List<byte>();
        Add16(versions, Pick());
        Add16(versions, 0x0304);
        Add16(versions, 0x0303);

        var settings = new List<byte>();
        Name(settings, "h2");

        var middle = new List<(ushort Type, IReadOnlyCollection<byte> Body)>
        {
            (0x0000, Block16(sni)),
            (0x0017, []),
            (0xFF01, [0x00]),
            (0x000A, Block16(groups)),
            (0x000B, [0x01, 0x00]),
            (0x0023, []),
            (0x0010, Block16(alpn)),
            (0x0005, [0x01, 0x00, 0x00, 0x00, 0x00]),
            (0x000D, Block16(signatures)),
            (0x0012, []),
            (0x0033, Block16(shares)),
            (0x002D, [0x01, 0x01]),
            (0x002B, Block8(versions)),

            // Сжатие сертификата, brotli. У SChannel его нет, а у браузеров есть
            // с 2020 года, и это второе по заметности отличие после шифров.
            (0x001B, [0x02, 0x00, 0x02]),

            // ALPS под новым номером: 0x4469 Chrome сменил на 0x44CD.
            (0x44CD, Block16(settings)),
            (0xFE0D, EchGrease()),
        };

        // Перемешивание как у Chrome 110+: каждое соединение по-своему.
        RandomNumberGenerator.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(middle));

        var list = new List<byte>();
        Extension(list, opening, []);

        foreach (var (type, body) in middle)
            Extension(list, type, body);

        // Добивки нет: с ключом ML-KEM приветствие и так под две тысячи байт,
        // а Chrome добивает только короткие.
        Extension(list, closing, [0x00]);

        return list;
    }

    /// <summary>
    /// Ключ ML-KEM-768, годный на вид: 768 коэффициентов меньше 3329 и 32 байта затравки.
    /// </summary>
    /// <remarks>
    /// Сторона проверяет ключ (FIPS 203, проверка модуля) и на негодный
    /// отвечает отказом — неотличимым от блокировки. Коэффициенты по два
    /// упакованы в три байта, младшими битами вперёд.
    /// </remarks>
    internal static byte[] MlKemKey()
    {
        var key = new byte[1184];

        for (int i = 0; i < 1152; i += 3)
        {
            int a = RandomNumberGenerator.GetInt32(3329);
            int b = RandomNumberGenerator.GetInt32(3329);

            key[i] = (byte)a;
            key[i + 1] = (byte)((a >> 8) | ((b & 0x0F) << 4));
            key[i + 2] = (byte)(b >> 4);
        }

        RandomNumberGenerator.Fill(key.AsSpan(1152));

        return key;
    }

    /// <summary>
    /// Пустышка ECH, какую шлёт Chrome без настоящего ключа ECH.
    /// </summary>
    /// <remarks>
    /// Внешнее приветствие: HKDF-SHA256, AES-128-GCM, случайный номер
    /// конфигурации, 32 байта «ключа» и шифртекст случайной длины из тех,
    /// что даёт Chrome. Сторона без ECH пропускает его мимо, как и положено
    /// незнакомому.
    /// </remarks>
    private static List<byte> EchGrease()
    {
        var body = new List<byte> { 0x00, 0x00, 0x01, 0x00, 0x01 };
        body.Add((byte)RandomNumberGenerator.GetInt32(256));
        body.AddRange(Block16(RandomNumberGenerator.GetBytes(32)));

        int[] sizes = [144, 176, 208, 240];
        body.AddRange(Block16(RandomNumberGenerator.GetBytes(sizes[RandomNumberGenerator.GetInt32(sizes.Length)])));

        return body;
    }

    /// <summary>
    /// Спрашивает <paramref name="via"/> своим приветствием.
    /// </summary>
    /// <remarks>
    /// По адресу, а не по имени: этот вопрос задаётся вторым, после того как
    /// системное приветствие уже отвергли, и адрес к тому времени известен —
    /// тот самый, который ответил на TCP. Разрешать имя заново значило бы
    /// спросить, чего доброго, другой узел и сравнить несравнимое.
    /// </remarks>
    public static async Task<HelloResult> AskAsync(
        IPAddress via,
        string host,
        TimeSpan connect,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(connect);

            await client.ConnectAsync(via, 443, limit.Token);

            var stream = client.GetStream();

            await stream.WriteAsync(Build(host), limit.Token);

            return await ReadAnswerAsync(stream, stopwatch, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new HelloResult(HelloAnswer.Reset, Short(ex), stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HelloResult(
                HelloAnswer.Silence, "ответа нет", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return new HelloResult(HelloAnswer.NoRoute, Short(ex), stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Читает первую запись ответа и больше ничего.
    /// </summary>
    /// <remarks>
    /// Больше и не нужно: тип записи и первый байт её содержимого отвечают
    /// на весь вопрос. <c>0x16</c> с <c>0x02</c> внутри — ServerHello, то есть
    /// приветствие принято; <c>0x15</c> — alert, то есть отказано словами.
    /// </remarks>
    internal static async Task<HelloResult> ReadAnswerAsync(
        Stream stream,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(Answer);

        var head = new byte[5];
        int got = 0;

        try
        {
            while (got < head.Length)
            {
                int read = await stream.ReadAsync(head.AsMemory(got), wait.Token);

                // Закрыли молча, не сказав ни слова. Отказ это или обрыв
                // по дороге — отсюда не видно, и выдумывать различие нельзя.
                if (read == 0)
                {
                    return new HelloResult(
                        got == 0 ? HelloAnswer.Silence : HelloAnswer.Reset,
                        got == 0
                            ? "закрыто без ответа"
                            : $"закрыто на {got} Б заголовка записи",
                        stopwatch.Elapsed);
                }

                got += read;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new HelloResult(HelloAnswer.Reset, Short(ex), stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HelloResult(HelloAnswer.Silence, "ответа нет", stopwatch.Elapsed);
        }

        if (head[0] == 0x15)
        {
            var alert = new byte[2];

            try
            {
                await stream.ReadExactlyAsync(alert, wait.Token);
            }
            catch (Exception)
            {
                return new HelloResult(
                    HelloAnswer.Refused, "отказ без подробностей", stopwatch.Elapsed);
            }

            return new HelloResult(HelloAnswer.Refused, Alert(alert[1]), stopwatch.Elapsed);
        }

        if (head[0] != 0x16)
        {
            // Не TLS вовсе. Так отвечает подставленная страница-заглушка:
            // на месте записи рукопожатия оказывается обычный текст.
            return new HelloResult(
                HelloAnswer.Reset,
                $"ответ не похож на TLS (запись типа 0x{head[0]:X2})",
                stopwatch.Elapsed);
        }

        var first = new byte[1];

        try
        {
            await stream.ReadExactlyAsync(first, wait.Token);
        }
        catch (Exception)
        {
            return new HelloResult(
                HelloAnswer.Reset, "запись рукопожатия оборвана", stopwatch.Elapsed);
        }

        return first[0] == 0x02
            ? new HelloResult(HelloAnswer.Accepted, "принято, ServerHello", stopwatch.Elapsed)
            : new HelloResult(
                HelloAnswer.Reset,
                $"вместо ServerHello пришло сообщение 0x{first[0]:X2}",
                stopwatch.Elapsed);
    }

    /// <summary>Имя отказа по коду alert.</summary>
    /// <remarks>
    /// Названы те, что встречаются на деле. Остальные показываются числом:
    /// придумывать перевод редкому коду значит прятать его настоящее имя,
    /// по которому только и можно что-то найти.
    /// </remarks>
    internal static string Alert(byte code) => code switch
    {
        40 => "handshake_failure — состав приветствия не принят",
        47 => "illegal_parameter — в приветствии что-то недопустимое",
        70 => "protocol_version — версия не поддерживается",
        71 => "insufficient_security — предложенные шифры слабы",
        80 => "internal_error на стороне сайта",
        109 => "missing_extension — не хватает обязательного расширения",
        110 => "unsupported_extension — лишнее расширение",
        112 => "unrecognized_name — сайт не знает такого имени",
        120 => "no_application_protocol — не сошлись на ALPN",
        _ => $"alert {code}",
    };

    private static string Short(Exception ex) =>
        ex is SocketException socket ? socket.SocketErrorCode.ToString() : ex.GetType().Name;

    private static ushort Pick() => Grease[RandomNumberGenerator.GetInt32(Grease.Length)];

    /// <summary>Две пустышки, заведомо разные.</summary>
    /// <remarks>
    /// Через сдвиг по кругу, а не перебором со сравнением: перебор пришлось бы
    /// ограничивать числом попыток, и ограничение это было бы взято с потолка.
    /// </remarks>
    private static (ushort Opening, ushort Closing) TwoGrease()
    {
        int first = RandomNumberGenerator.GetInt32(Grease.Length);
        int step = 1 + RandomNumberGenerator.GetInt32(Grease.Length - 1);

        return (Grease[first], Grease[(first + step) % Grease.Length]);
    }

    private static void Add16(List<byte> list, ushort value)
    {
        list.Add((byte)(value >> 8));
        list.Add((byte)value);
    }

    private static void Name(List<byte> list, string protocol)
    {
        list.Add((byte)protocol.Length);
        list.AddRange(Encoding.ASCII.GetBytes(protocol));
    }

    private static void Extension(List<byte> list, ushort type, IReadOnlyCollection<byte> body)
    {
        Add16(list, type);
        list.AddRange(Block16(body));
    }

    private static List<byte> Block8(IReadOnlyCollection<byte> body) =>
        [(byte)body.Count, .. body];

    private static List<byte> Block16(IReadOnlyCollection<byte> body) =>
        [(byte)(body.Count >> 8), (byte)body.Count, .. body];

    private static List<byte> Block24(IReadOnlyCollection<byte> body) =>
        [(byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count, .. body];
}
