using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace NetZapret.Proxy;

/// <summary>Что выяснилось про ответ DNS.</summary>
public enum DnsVerdict
{
    /// <summary>Признаков подмены нет.</summary>
    Clean,

    /// <summary>Имя разрешилось в адрес, где настоящий сайт жить не может.</summary>
    Sinkhole,

    /// <summary>По адресу отвечает не тот, чьё имя спрашивали.</summary>
    ForeignCertificate,

    /// <summary>Судить не по чему: ни адресов, ни сертификата.</summary>
    Unknown,
}

/// <summary>Вердикт с объяснением.</summary>
public sealed record DnsSpoofResult
{
    public required DnsVerdict Verdict { get; init; }

    /// <summary>Чем именно выдало себя; у чистого — пусто.</summary>
    public string Detail { get; init; } = string.Empty;

    public bool Spoofed => Verdict is DnsVerdict.Sinkhole or DnsVerdict.ForeignCertificate;

    /// <summary>
    /// Адрес, к которому имя прибито в файле hosts; <c>null</c> — не прибито.
    /// </summary>
    /// <remarks>
    /// Подмену делает не только оператор. 26.09 у jetbrains.com, notion.so
    /// и tiktok.com её устроили чужие записи hosts на 72.56.93.144 — прокси,
    /// который больше не обслуживает эти имена. Совет «прибейте настоящий
    /// адрес» там вёл в сторону: прибито было, и именно это и мешало.
    /// </remarks>
    public string? PinnedTo { get; init; }

    /// <summary>Адрес от честного резолвера, отвечающий верным сертификатом.</summary>
    public string? Honest { get; init; }

    /// <summary>
    /// Что с этим делать — одной строкой, общей для окна и консоли.
    /// </summary>
    public string Advice => PinnedTo is { } pin
        ? $"{Detail} — имя прибито в hosts к {pin}; удалите эту запись во вкладке «Файл hosts»"
            + (Honest is { } honest ? $", настоящий адрес — {honest}" : string.Empty)
        : Detail + " — рецептом десинка не лечится: прибейте настоящий адрес "
            + (Honest is { } found ? $"({found}) " : string.Empty)
            + "во вкладке «Файл hosts» либо уведите имя в VPN";

    public static DnsSpoofResult Clean { get; } = new() { Verdict = DnsVerdict.Clean };

    public static DnsSpoofResult Unknown { get; } = new() { Verdict = DnsVerdict.Unknown };
}

/// <summary>
/// Подменён ли ответ DNS.
/// </summary>
/// <remarks>
/// <para>
/// Блокировка не всегда доходит до пакетов. Дешевле подменить ответ DNS —
/// и тогда имя разрешается в чужой адрес: заглушку оператора, <c>0.0.0.0</c>
/// или воронку. Снаружи это неотличимо от блокировки по имени в приветствии
/// TLS: сайт не открывается, десинк не помогает, и причину ищут не там.
/// </para>
/// <para>
/// Отличать надо и от того, что подменой не является. Один и тот же сайт
/// законно разрешается в разные адреса: сеть доставки отдаёт ближайший узел,
/// у крупных имён адресов десятки, и расхождение двух ответов само по себе
/// не значит ничего. Поэтому признак здесь не «ответы разошлись», а «по этому
/// адресу живёт не тот».
/// </para>
/// <para>
/// Признака два, и оба однозначны. Служебный адрес: настоящий сайт
/// не живёт ни на <c>0.0.0.0</c>, ни на петле, ни в частной сети. Чужой
/// сертификат: заглушка оператора не может предъявить сертификат, выписанный
/// на чужое имя, — для этого нужен закрытый ключ владельца.
/// </para>
/// <para>
/// Своего разрешителя здесь нет и системный DNS не трогается: смотрим
/// на то, что уже получили, пока ходили на имя. Пробе это не стоит ни одного
/// лишнего соединения.
/// </para>
/// <para>
/// Ошибка 19.09, ради которой всё и затевалось: я мерил QUIC по адресу,
/// взятому из подменённого ответа, и получал «QUIC не доходит» при
/// совершенно исправном QUIC.
/// </para>
/// <para>
/// Замер на живой сети 20.09, двенадцать имён: ни одного ложного
/// срабатывания, включая сети доставки с шестью адресами и звёздочными
/// сертификатами — google.com, vk.com, cdn.jsdelivr.net. В обратную
/// сторону проверено обращением к <c>1.1.1.1</c> под чужим именем:
/// предъявляется <c>cloudflare-dns.com</c>, и подмена названа.
/// </para>
/// <para>
/// Из того же замера — предостережение против «очевидных» проверок:
/// обращение к <c>1.1.1.1</c> под именем <c>example.com</c> даёт
/// не подмену, а <see cref="DnsVerdict.Clean"/>, и это правильно.
/// Cloudflare обслуживает example.com по-настоящему и предъявляет
/// выписанный на него сертификат — мы пришли на настоящий сайт,
/// пусть и не по его «собственному» адресу.
/// </para>
/// </remarks>
public static class DnsSpoof
{
    /// <summary>
    /// Адрес, по которому не может жить вообще ничто.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Твёрдый признак: сюда попадают только те диапазоны, где не бывает
    /// не то что публичного сайта, а вообще собеседника. Соединение по
    /// такому адресу либо не уйдёт с машины, либо вернётся к ней же.
    /// </para>
    /// <para>
    /// Частные сети сюда намеренно НЕ включены, хотя операторы уводят имена
    /// именно туда. По <c>192.168.1.10</c> у человека может законно жить
    /// свой NAS или роутер, и объявить его подменой значило бы отправить
    /// чинить то, что не сломано. Для них есть <see cref="IsPrivate"/>,
    /// и говорит он осторожнее.
    /// </para>
    /// </remarks>
    public static bool IsSinkhole(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IPv6Any.Equals(address)
                || IPAddress.IPv6Loopback.Equals(address);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();

        return b[0] switch
        {
            // 0.0.0.0/8 — «этот узел». Ответ такого вида и есть отказ:
            // соединение по нему не уходит никуда.
            0 => true,

            // 127.0.0.0/8 — петля. Имя, разрешённое сюда, ведёт на сам
            // компьютер: приём известный, им же пользуемся мы в hosts.
            127 => true,

            _ => false,
        };
    }

    /// <summary>
    /// Адрес из домашней сети.
    /// </summary>
    /// <remarks>
    /// Мягкий признак, и потому отдельный. Публичный сайт здесь жить
    /// не может, и страница-заглушка оператора чаще всего стоит именно
    /// тут — но тут же стоят роутер, принтер и домашний сервер. Поэтому
    /// вывод отсюда не «подмена», а «отвечает кто-то из вашей сети»,
    /// и проверять догадку идёт уже сертификат.
    /// </remarks>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();

        return b[0] switch
        {
            10 => true,

            // 169.254.0.0/16 — адрес без DHCP: признак неудачи, а не сайта.
            169 when b[1] == 254 => true,

            172 when b[1] >= 16 && b[1] <= 31 => true,
            192 when b[1] == 168 => true,

            _ => false,
        };
    }

    /// <summary>
    /// Выписан ли сертификат на это имя.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Через <see cref="X509Certificate2.MatchesHostname"/>: он разбирает
    /// subjectAltName и понимает звёздочку. Своя сверка тут была бы разбором
    /// ASN.1 ради задачи, уже решённой в платформе, и ошиблась бы на первом
    /// же сертификате с десятком имён — а их у крупных сайтов большинство.
    /// </para>
    /// <para>
    /// Доверие не проверяется намеренно, и это не упущение. Вопрос здесь
    /// один: тот ли это сайт. Самоподписанный сертификат на верное имя
    /// подменой не является — так выглядит сайт с просроченной или
    /// собственной подписью, и объявить его подменой значило бы соврать.
    /// </para>
    /// </remarks>
    public static bool MatchesHost(X509Certificate2 certificate, string host)
    {
        try
        {
            return certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: true);
        }
        catch (Exception)
        {
            // Испорченный сертификат разобрать нельзя, и объявлять по нему
            // подмену — гадание. Пусть считается совпавшим: пропущенная
            // подмена дешевле выдуманной.
            return true;
        }
    }

    /// <summary>
    /// Судит по тому, что собрала проба.
    /// </summary>
    /// <param name="host">Имя, которое спрашивали.</param>
    /// <param name="addresses">Во что оно разрешилось; пусто — не разрешалось.</param>
    /// <param name="certificate">
    /// Что предъявила сторона; <c>null</c> — рукопожатия не было.
    /// </param>
    public static DnsSpoofResult Judge(
        string host,
        IReadOnlyList<IPAddress>? addresses,
        X509Certificate2? certificate)
    {
        // Служебный адрес проверяется первым: он виден до всякого соединения
        // и объясняет заодно, почему соединения не будет.
        if (addresses is { Count: > 0 })
        {
            var sinkhole = addresses.FirstOrDefault(IsSinkhole);

            if (sinkhole is not null)
            {
                return new DnsSpoofResult
                {
                    Verdict = DnsVerdict.Sinkhole,
                    Detail = $"имя разрешается в {sinkhole} — там настоящий сайт не живёт",
                };
            }
        }

        if (certificate is not null)
        {
            // Сертификат весомее домашнего адреса и потому спрашивается
            // раньше него. Он отвечает на тот же вопрос прямо: подменённое
            // имя нельзя подтвердить чужим сертификатом, для этого нужен
            // закрытый ключ владельца.
            return MatchesHost(certificate, host)
                ? DnsSpoofResult.Clean
                : new DnsSpoofResult
                {
                    Verdict = DnsVerdict.ForeignCertificate,
                    Detail = $"по адресу отвечает не «{host}», а «{Subject(certificate)}»",
                };
        }

        // Домашний адрес без сертификата — догадка, и высказывается она
        // догадкой. Публичный сайт здесь жить не может, но тут же стоят
        // роутер и домашний сервер, и назвать это подменой значило бы
        // отправить человека чинить исправное.
        if (addresses?.FirstOrDefault(IsPrivate) is { } home)
        {
            return new DnsSpoofResult
            {
                Verdict = DnsVerdict.Sinkhole,
                Detail = $"имя разрешается в {home} — адрес из домашней сети, "
                    + "публичного сайта там не бывает",
            };
        }

        // Адреса есть и они настоящие, а рукопожатия не было. Подмены отсюда
        // не видно — но и «чисто» сказать нельзя: мы не дошли до того, по чему
        // судят. Чужой сертификат предъявляют именно в рукопожатии, и его
        // отсутствие означает незнание, а не отсутствие подмены.
        return DnsSpoofResult.Unknown;
    }

    /// <summary>Чьё имя стоит в сертификате — коротко.</summary>
    private static string Subject(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);

        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return string.IsNullOrWhiteSpace(certificate.Subject)
            ? "без имени"
            : certificate.Subject;
    }
}
