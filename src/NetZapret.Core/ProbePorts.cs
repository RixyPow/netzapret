using System.Net;
using System.Net.Sockets;

namespace NetZapret.Core;

/// <summary>
/// Локальные порты, с которых проба рецепта открывает соединения: основной
/// winws2 их не перехватывает, пробный — только их.
/// </summary>
/// <remarks>
/// <para>
/// Рецепт нельзя проверить, пока работает десинк: второй winws2 обрабатывал бы
/// те же пакеты поверх первого, и вышел бы замер не рецепта, а их суммы.
/// Окно рецептов поэтому требовало остановить движки. Разводит их порт:
/// основной фильтр исключает эти порты (<c>TunnelCapture</c>), пробный
/// перехватывает только их, и проба ходит строго с них (владелец 27.10:
/// «отдельный winws2»).
/// </para>
/// <para>
/// Ниже динамического диапазона Windows (49152–65535), из которого берут
/// порты обычные соединения: выпади им наш номер, они молча шли бы мимо
/// десинка. Сто портов — на целый подбор: закрытие сбросом (без TIME_WAIT)
/// освобождает порт сразу, а занятый пропускается.
/// </para>
/// </remarks>
public static class ProbePorts
{
    public const int First = 39100;

    public const int Last = 39199;

    /// <summary>Пакеты пробы для фильтра WinDivert: исходящие с её портов и входящие на них.</summary>
    public static string Packets =>
        $"(tcp.SrcPort>={First} and tcp.SrcPort<={Last}) or (tcp.DstPort>={First} and tcp.DstPort<={Last})";

    private static readonly AsyncLocal<bool> InUse = new();

    private static int _next = First - 1;

    /// <summary>Идёт ли сейчас проба с портов пробы — в этом потоке исполнения.</summary>
    public static bool Active => InUse.Value;

    /// <summary>
    /// Пока жив возвращённый объект, соединения проб в этом потоке исполнения
    /// (и во всём, что из него ожидается) открываются с портов пробы.
    /// </summary>
    /// <remarks>
    /// Потоком исполнения, а не параметром: соединения открывают с десяток
    /// мест проверки блокировок, и тащить порт через каждое значило бы
    /// переписать их все ради одного вызова.
    /// </remarks>
    public static IDisposable Use()
    {
        bool before = InUse.Value;
        InUse.Value = true;
        return new Scope(before);
    }

    /// <summary>
    /// TCP-клиент для пробы: под <see cref="Use"/> — привязанный к свободному
    /// порту пробы и закрываемый сбросом; иначе — обычный.
    /// </summary>
    public static TcpClient Client(AddressFamily family)
    {
        var client = new TcpClient(family);

        if (!Active)
            return client;

        var any = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;

        for (int tried = 0; tried <= Last - First; tried++)
        {
            int port = First + (int)((uint)Interlocked.Increment(ref _next) % (uint)(Last - First + 1));

            try
            {
                client.Client.Bind(new IPEndPoint(any, port));

                // Сброс вместо FIN: порт не застревает в TIME_WAIT на две минуты,
                // и следующий рецепт берёт его сразу.
                client.LingerState = new LingerOption(true, 0);
                return client;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // Занят или зарезервирован — следующий.
            }
        }

        client.Dispose();
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private sealed class Scope(bool before) : IDisposable
    {
        public void Dispose() => InUse.Value = before;
    }
}
