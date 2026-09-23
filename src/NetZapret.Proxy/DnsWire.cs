using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetZapret.Proxy;

/// <summary>
/// Сообщения DNS в проводном формате: собрать запрос, разобрать ответ.
/// </summary>
/// <remarks>
/// Вручную, без библиотеки, как и в <see cref="DnsProbe"/>: заголовок
/// в двенадцать байт, имя по меткам, записи подряд. Разбор умеет сжатие
/// имён — без него ответ почти любого резолвера читался бы мусором.
/// </remarks>
public static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeTxt = 16;

    /// <summary>Запрос с рекурсией; идентификатор случайный.</summary>
    public static byte[] Query(string name, ushort type)
    {
        var body = new List<byte>();
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);

        body.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);

        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }

        body.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);

        return [.. body];
    }

    /// <summary>Разобранный ответ.</summary>
    public sealed record Answer(int Code, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> Texts);

    /// <summary>Разбирает ответ; <c>null</c> — это не ответ DNS.</summary>
    public static Answer? Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12 || (message[2] & 0x80) == 0)
            return null;

        int code = message[3] & 0x0F;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        var addresses = new List<IPAddress>();
        var texts = new List<string>();
        int at = 12;

        try
        {
            for (int i = 0; i < questions; i++)
                at = SkipName(message, at) + 4;

            for (int i = 0; i < answers; i++)
            {
                at = SkipName(message, at);

                ushort type = BinaryPrimitives.ReadUInt16BigEndian(message[at..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(message[(at + 8)..]);
                var data = message.Slice(at + 10, length);

                if (type == TypeA && length == 4)
                {
                    addresses.Add(new IPAddress(data));
                }
                else if (type == TypeTxt)
                {
                    // TXT — одна или несколько строк с длиной впереди.
                    var text = new StringBuilder();

                    for (int j = 0; j < data.Length;)
                    {
                        int part = data[j];
                        text.Append(Encoding.ASCII.GetString(data.Slice(j + 1, part)));
                        j += part + 1;
                    }

                    texts.Add(text.ToString());
                }

                at += 10 + length;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Обрезанный ответ: что успели прочитать — то и есть.
        }

        return new Answer(code, addresses, texts);
    }

    /// <summary>Пропускает имя, включая сжатое ссылкой.</summary>
    private static int SkipName(ReadOnlySpan<byte> message, int at)
    {
        while (true)
        {
            byte length = message[at];

            if (length == 0)
                return at + 1;

            // Ссылка — два байта, и имя на ней кончается.
            if ((length & 0xC0) == 0xC0)
                return at + 2;

            at += length + 1;
        }
    }
}
