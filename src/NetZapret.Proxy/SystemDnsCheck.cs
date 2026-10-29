using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Что резолвер системы отвечает на имена, которые есть у всех.</summary>
public enum SystemDnsKind
{
    /// <summary>Не узнали: резолвера нет, не ответил или ответил ошибкой сервера.</summary>
    Unknown,

    /// <summary>Отдаёт адреса.</summary>
    Honest,

    /// <summary>Говорит «нет такого имени» или отдаёт заглушку на имена, которые есть.</summary>
    Forged,
}

/// <summary>Вердикт по резолверу системы: какой, что с ним и на каких именах.</summary>
public sealed record SystemDnsVerdict(SystemDnsKind Kind, string? Resolver, IReadOnlyList<string> Names);

/// <summary>
/// Подменяет ли DNS провайдера ответы — для «Десинка», где имена разрешает сама Windows.
/// </summary>
/// <remarks>
/// <para>
/// Без туннеля и без DNS через движок (<see cref="AppSettings.NeedsDnsEngine"/>)
/// десинку достаются имена от резолвера провайдера. Вики zapret.moe
/// (DPI/nsdi-domain-removal-2026, DPI/tspu-dns-nsdi-dnat-august-2026,
/// DPI/ttk-youtube-ggc-october-2026): с 10.02.2026 youtube.com убран из НСДИ,
/// 28.08 резолвер НСДИ на youtube.com, www.instagram.com — NXDOMAIN, 08.10
/// youtube.com и rutracker.org — NXDOMAIN у 12 зондов из 12; с августа
/// открытый 8.8.8.8 и 1.1.1.1 у части абонентов заворачивают туда же.
/// Человек видит «сайт не найден» и винит десинк, а «Диагностика» лишь
/// перечисляла резолверы. У владельца это записано с 03.10 как «не проверено».
/// </para>
/// <para>
/// Судим без эталона: у этих имён адреса есть всегда, поэтому «нет такого
/// имени», пустой ответ или адрес-заглушка — подмена сами по себе. Честный
/// DoH здесь не нужен — его с августа самого режут по имени сервера.
/// </para>
/// </remarks>
public static class SystemDnsCheck
{
    /// <summary>Имена, у которых адрес есть всегда, а НСДИ их не отдаёт (вики, 28.08 и 08.10).</summary>
    public static readonly IReadOnlyList<string> Probes = ["www.youtube.com", "rutracker.org", "www.instagram.com"];

    /// <summary>Выдаёт ли ответ подмену — без эталона, по самому ответу.</summary>
    /// <remarks>
    /// RCODE 3 — «нет такого имени»; 0 без единого адреса — пусто; адрес из
    /// служебной или частной сети — заглушка (<see cref="DnsSpoof"/>). Отказ
    /// сервера (SERVFAIL, REFUSED) подменой не считается: так отвечает
    /// и сломанный резолвер, а не только цензурный.
    /// </remarks>
    public static bool LooksForged(DnsWire.Answer answer) =>
        answer.Code == 3
        || answer.Code == 0 && answer.Addresses.Count == 0
        || answer.Code == 0 && answer.Addresses.Any(a => DnsSpoof.IsSinkhole(a) || DnsSpoof.IsPrivate(a));

    /// <summary>
    /// Спрашивает первый резолвер системы по UDP через физический адаптер.
    /// </summary>
    /// <remarks>
    /// Один резолвер и три имени по одному: это три пакета, а не замер.
    /// Сокет привязан к адаптеру — при поднятом туннеле ответил бы наш fakeip;
    /// но звать это и нужно только тогда, когда туннеля нет.
    /// </remarks>
    public static async Task<SystemDnsVerdict> CheckAsync(CancellationToken cancellationToken)
    {
        string? resolver;

        try
        {
            resolver = SystemResolvers.Discover()
                .Select(r => r.Split('/')[0])
                .FirstOrDefault(r => System.Net.IPAddress.TryParse(r, out var ip)
                    && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
        catch (Exception)
        {
            resolver = null;
        }

        if (resolver is null)
            return new(SystemDnsKind.Unknown, null, []);

        return await CheckAsync(resolver, DnsSurvey.PhysicalInterface(), cancellationToken);
    }

    /// <summary>То же для заданного резолвера.</summary>
    public static async Task<SystemDnsVerdict> CheckAsync(string resolver, int? nic, CancellationToken cancellationToken)
    {
        var forged = new List<string>();
        int honest = 0;

        foreach (var name in Probes)
        {
            var (answer, _, _) = await DnsSurvey.UdpAsync(resolver, name, DnsWire.TypeA, nic, cancellationToken);

            if (answer is null)
                continue;

            if (LooksForged(answer))
                forged.Add(name);
            else if (answer.Code == 0)
                honest++;
        }

        return forged.Count > 0 ? new(SystemDnsKind.Forged, resolver, forged)
            : honest > 0 ? new(SystemDnsKind.Honest, resolver, [])
            : new(SystemDnsKind.Unknown, resolver, []);
    }

    /// <summary>Нужна ли проверка при этих настройках: имена разрешает Windows, а не движок.</summary>
    public static bool Applies(AppSettings settings) =>
        settings.NeedsDesync && !settings.NeedsProxy && !settings.NeedsDnsEngine;

    /// <summary>Что сказать человеку о подмене — одни слова для «Главной» и «Диагностики».</summary>
    public static string Advice(SystemDnsVerdict verdict) =>
        $"Резолвер {verdict.Resolver} на {string.Join(", ", verdict.Names)} отвечает «нет такого имени» или заглушкой — "
        + "так отвечает DNS провайдера (НСДИ). В «Десинке» имена спрашивает сама Windows, и эти сайты не откроются, "
        + "как бы ни работал десинк. На вкладке DNS выберите путь «через туннель»: без VPN это значит, "
        + "что имена спрашивает движок по шифрованному DNS. Перезапустите движки.";
}
