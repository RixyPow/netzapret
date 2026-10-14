using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ответ кусками дочитан, когда пришёл последний, пустой кусок.
/// </summary>
/// <remarks>
/// Та же беда, что у ответов без тела (<see cref="BodylessAnswerTests"/>):
/// проба читала до закрытия соединения и падала на нём. 14.10 reborder.app
/// (Next.js на Vercel, через туннель): «оборвано на 3844 Б» восемь раз
/// из восьми, а 3844 Б — весь ответ; curl получал страницу шесть из шести.
/// </remarks>
public sealed class ChunkedEndTests
{
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>Хвост потока, собранный по чтениям, — как в пробе.</summary>
    private static byte[] Feed(params string[] reads)
    {
        var tail = Array.Empty<byte>();

        foreach (var read in reads)
        {
            var buffer = Bytes(read);
            tail = BlockCheck.Tail(tail, buffer, buffer.Length, BlockCheck.LastChunk.Length);
        }

        return tail;
    }

    [Fact]
    public void The_last_chunk_ends_the_answer_even_split_between_reads()
    {
        var tail = Feed(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Encoding: br\r\n\r\n",
            "c6b\r\n<сжатое тело>\r\n",
            "a\r\n0123456789\r\n0\r",
            "\n\r\n");

        Assert.True(tail.AsSpan().SequenceEqual(BlockCheck.LastChunk));
    }

    [Fact]
    public void A_chunk_in_the_middle_is_not_the_end()
    {
        var tail = Feed(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n",
            "10\r\n0123456789abcdef\r\n");

        Assert.False(tail.AsSpan().SequenceEqual(BlockCheck.LastChunk));
    }

    /// <summary>Пустое тело кусками — сразу последний кусок за заголовками.</summary>
    [Fact]
    public void An_empty_chunked_body_is_recognised()
    {
        var tail = Feed("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");

        Assert.True(tail.AsSpan().SequenceEqual(BlockCheck.LastChunk));
    }

    [Fact]
    public void The_tail_keeps_only_the_last_bytes()
    {
        var tail = Feed("abc", "defghij", "k");

        Assert.Equal("efghijk", Encoding.ASCII.GetString(tail));
        Assert.Equal("ab", Encoding.ASCII.GetString(Feed("ab")));
    }
}
