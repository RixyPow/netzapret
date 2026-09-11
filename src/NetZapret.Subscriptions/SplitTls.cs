using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetZapret.Subscriptions;

/// <summary>
/// Соединение, у которого приветствие TLS разрезано надвое.
/// </summary>
/// <remarks>
/// <para>
/// Тот же приём, которым живёт десинк, только применённый к собственному
/// запросу программы. Оператор закрывает домен по имени в открытой части
/// приветствия TLS: TCP устанавливается, имя опознаётся, дальше соединение
/// глохнет. Если разрезать приветствие посреди имени, каждая половина
/// проходит проверку по отдельности, а сервер собирает их обратно сам —
/// разрыв на границе сегментов TCP для него не значит ничего.
/// </para>
/// <para>
/// Место разреза выбирается не наугад, а по самому имени: мы видим байты,
/// которые собрался отправить <see cref="System.Net.Security.SslStream"/>,
/// и режем ровно посередине домена. Замер 2026-09-11 на api.cloudflareclient.com:
/// без разреза рукопожатие не доходит ни разу, разрез на 143-м байте —
/// посередине имени — открывает его с первой попытки. Разрезы ближе
/// к началу (1, 2, 5, 20, 64) не помогают: имя остаётся целым во второй
/// половине, и его по-прежнему видно.
/// </para>
/// <para>
/// Заведено ради регистрации WARP. Она нужна там, где нужен запасной выход,
/// то есть где обычные пути уже не работают, и требовать для неё поднятого
/// туннеля значило бы выдавать зонт только в сухую погоду.
/// </para>
/// </remarks>
public static class SplitTls
{
    /// <summary>Пауза между половинами — чтобы они точно ушли разными пакетами.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(40);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Обработчик запросов, режущий приветствие TLS.
    /// </summary>
    /// <remarks>
    /// Разрез делается в <c>ConnectCallback</c>: он отдаёт поток, поверх
    /// которого .NET сам поднимает TLS, и подменить нужно именно его — после
    /// рукопожатия резать уже нечего.
    /// </remarks>
    public static HttpMessageHandler Handler() => new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellationToken) =>
            await ConnectAsync(context.DnsEndPoint, cancellationToken),
    };

    /// <summary>
    /// Соединяется, перебирая адреса имени.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Перебор нужен: после неудачного приветствия оператор какое-то время
    /// не пускает к тому же адресу вовсе — отказ приходит уже на установлении
    /// TCP. У Cloudflare адресов у одного имени несколько, и следующий обычно
    /// отвечает.
    /// </para>
    /// <para>
    /// IPv4 идёт первым намеренно. Разрешение имени отдаёт IPv6 раньше,
    /// и на машине без маршрута IPv6 — а это большинство домашних подключений
    /// в России — каждая попытка съедала ожидание целиком, прежде чем дойти
    /// до адреса, который работает. Со стороны это выглядело как «Cloudflare
    /// не отвечает», хотя не отвечал он ни разу: до него не доходило.
    /// </para>
    /// </remarks>
    private static async Task<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var addresses = (await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken))
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6)
            .ToList();

        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                // Обязателен: без него две половины приветствия склеятся
                // в один пакет, и весь смысл разреза пропадёт.
                NoDelay = true,
            };

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(ConnectTimeout);

                await socket.ConnectAsync(address, endPoint.Port, deadline.Token);

                return new SplitStream(new NetworkStream(socket, ownsSocket: true), endPoint.Host);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>
    /// Поток, который делит первую запись надвое по середине имени.
    /// </summary>
    private sealed class SplitStream(Stream inner, string host) : Stream
    {
        private readonly byte[] _marker = Encoding.ASCII.GetBytes(host);
        private bool _passed;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_passed)
            {
                _passed = true;

                if (SplitPoint(buffer.Span) is { } at)
                {
                    await inner.WriteAsync(buffer[..at], cancellationToken);
                    await inner.FlushAsync(cancellationToken);
                    await Task.Delay(Pause, cancellationToken);
                    await inner.WriteAsync(buffer[at..], cancellationToken);
                    await inner.FlushAsync(cancellationToken);

                    return;
                }
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_passed)
            {
                _passed = true;

                if (SplitPoint(buffer) is { } at)
                {
                    inner.Write(buffer[..at]);
                    inner.Flush();
                    Thread.Sleep(Pause);
                    inner.Write(buffer[at..]);
                    inner.Flush();

                    return;
                }
            }

            inner.Write(buffer);
        }

        /// <summary>
        /// Где резать; <c>null</c> — имени в записи нет, резать незачем.
        /// </summary>
        /// <remarks>
        /// Имя ищется как обычная строка ASCII: в приветствии TLS оно лежит
        /// открытым текстом, ради чего его и читает оператор.
        /// </remarks>
        private int? SplitPoint(ReadOnlySpan<byte> buffer)
        {
            if (_marker.Length < 4)
                return null;

            int found = buffer.IndexOf(_marker);

            if (found < 0)
                return null;

            int at = found + _marker.Length / 2;

            return at > 0 && at < buffer.Length ? at : null;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override bool CanRead => inner.CanRead;

        public override bool CanWrite => inner.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
