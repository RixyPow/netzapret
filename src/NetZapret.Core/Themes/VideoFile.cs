namespace NetZapret.Core.Themes;

/// <summary>
/// Размер кадра видео mp4 — из заголовка, без проигрывателя.
/// </summary>
/// <remarks>
/// Нужен окну до того, как видео открылось: кисть фона и колонка арта
/// раскладываются по пропорциям кадра сразу при применении темы, а
/// проигрыватель узнаёт размер только после открытия файла, когда
/// раскладка уже сделана. Читается дорожка из moov → trak → tkhd: у звука
/// размер нулевой, у видео — ширина и высота в формате 16.16.
/// </remarks>
public static class VideoFile
{
    /// <summary>Ширина и высота кадра; <c>null</c> — файл не читается или не mp4.</summary>
    public static (int Width, int Height)? FrameSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return FrameSize(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>То же по потоку — для проверки без файла.</summary>
    public static (int Width, int Height)? FrameSize(Stream stream) => Walk(stream, 0, stream.Length, depth: 0);

    private static (int Width, int Height)? Walk(Stream stream, long start, long end, int depth)
    {
        // Глубже moov → trak → tkhd искать нечего, а битый файл не должен
        // увести в бесконечную вложенность.
        if (depth > 3)
            return null;

        var header = new byte[16];
        long at = start;

        while (at + 8 <= end)
        {
            stream.Position = at;

            if (!Fill(stream, header, 8))
                return null;

            long size = ReadUInt32(header, 0);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            int headerSize = 8;

            if (size == 1)
            {
                if (!Fill(stream, header, 8))
                    return null;

                size = (long)ReadUInt64(header, 0);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - at;
            }

            if (size < headerSize || at + size > end)
                return null;

            long body = at + headerSize;

            if (type is "moov" or "trak")
            {
                if (Walk(stream, body, at + size, depth + 1) is { } found)
                    return found;
            }
            else if (type == "tkhd" && Track(stream, body, at + size) is { } frame)
            {
                return frame;
            }

            at += size;
        }

        return null;
    }

    private static (int Width, int Height)? Track(Stream stream, long body, long end)
    {
        stream.Position = body;
        var head = new byte[1];

        if (!Fill(stream, head, 1))
            return null;

        // Версия 1 — времена по 8 байт, версия 0 — по 4: ширина и высота
        // стоят в конце коробки, после матрицы преобразования.
        long offset = head[0] == 1 ? 88 : 76;

        if (body + offset + 8 > end)
            return null;

        stream.Position = body + offset;
        var size = new byte[8];

        if (!Fill(stream, size, 8))
            return null;

        int width = (int)(ReadUInt32(size, 0) >> 16);
        int height = (int)(ReadUInt32(size, 4) >> 16);

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static bool Fill(Stream stream, byte[] buffer, int count)
    {
        int read = 0;

        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);

            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static ulong ReadUInt64(byte[] data, int offset) =>
        (ulong)ReadUInt32(data, offset) << 32 | ReadUInt32(data, offset + 4);
}
