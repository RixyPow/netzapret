using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;

// System.Net.Quic в .NET 8 помечен предварительным (стабилен с .NET 9). Включать
// EnablePreviewFeatures нельзя: пометка легла бы на всю библиотеку и на всех,
// кто на неё ссылается. Здесь — одна проба, и при любой неудаче QUIC она
// молча уступает (IsSupported, null), так что глушится правило только тут.
#pragma warning disable CA2252

namespace NetZapret.Proxy;

/// <summary>
/// Рукопожатие QUIC с тем же адресом и тем же именем, что у пробы по TCP.
/// </summary>
/// <remarks>
/// <para>
/// Проверка блокировок мерила только TCP, а браузер к сайту, объявившему HTTP/3,
/// идёт и по QUIC. DPI обрабатывает UDP хуже и непредсказуемее TCP, часть
/// потоков молча режет (вики zapret.moe, «Отключение QUIC как обход таймаутов
/// ТСПУ», 27.10), и браузер ждёт: 15.10 YouTube подвисал в Хроме, хотя по TCP
/// шёл на 187 Мбит/с, — QUIC провайдер резал по имени, а подделка QUIC в
/// пресетах стояла только по адресам. Проба этого не видела вовсе.
/// </para>
/// <para>
/// Только рукопожатие, без запроса: вопрос — доходит ли QUIC с этим именем
/// до стороны. Ответ стороны словами (отказ TLS внутри QUIC) — тоже «дошло».
/// </para>
/// </remarks>
public static class QuicProbe
{
    /// <summary>
    /// Спрашивает <paramref name="via"/> рукопожатием QUIC с именем <paramref name="host"/>;
    /// <c>null</c> — QUIC на этой машине недоступен (msquic, Windows 10 без TLS 1.3 в Schannel).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5359:Do not disable certificate validation",
        Justification = "Измеряется доходимость рукопожатия, а не доверие.")]
    public static async Task<ProbeOutcome?> AskAsync(
        IPAddress via,
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
            return null;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            await using var connection = await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new IPEndPoint(via, 443),

                    // Коды HTTP/3 (RFC 9114): закрываем без ошибки — спросили
                    // и уходим.
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,

                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        ApplicationProtocols = [new SslApplicationProtocol("h3")],
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    },
                },
                limit.Token);

            return new ProbeOutcome
            {
                Ok = true,
                Elapsed = stopwatch.Elapsed,
                Detail = $"рукопожатие QUIC за {stopwatch.Elapsed.TotalMilliseconds:0} мс",
            };
        }
        catch (AuthenticationException ex)
        {
            // Сторона ответила и отказала внутри TLS — путь QUIC исправен.
            return new ProbeOutcome
            {
                Ok = true,
                Elapsed = stopwatch.Elapsed,
                Detail = $"QUIC доходит, сторона отказала в TLS: {ex.Message}",
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Elapsed = stopwatch.Elapsed,
                Detail = $"QUIC молчит {timeout.TotalSeconds:0} с",
            };
        }
        catch (QuicException ex) when (ex.QuicError is QuicError.ConnectionTimeout or QuicError.ConnectionIdle)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Elapsed = stopwatch.Elapsed,
                Detail = $"QUIC молчит, {stopwatch.Elapsed.TotalSeconds:0.0} с",
            };
        }
        catch (QuicException ex)
        {
            // Ответ, а не тишина: браузер получит его так же быстро и уйдёт
            // на TCP, не подвисая. Замер 27.10: www.google.com и i.ytimg.com
            // закрывают QUIC с PROTOCOL_VIOLATION (0xA) за 30–75 мс — отчего,
            // не установлено, но это не «QUIC молчит».
            return new ProbeOutcome
            {
                Ok = true,
                Elapsed = stopwatch.Elapsed,
                Detail = ex.TransportErrorCode is { } code
                    ? $"QUIC доходит, сторона закрыла с ошибкой 0x{code:X} за {stopwatch.Elapsed.TotalMilliseconds:0} мс"
                    : $"QUIC доходит, отказ {ex.QuicError} за {stopwatch.Elapsed.TotalMilliseconds:0} мс",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Elapsed = stopwatch.Elapsed,
                Detail = $"QUIC не встал: {ex.GetBaseException().Message}",
            };
        }
    }
}
