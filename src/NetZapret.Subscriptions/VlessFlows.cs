namespace NetZapret.Subscriptions;

/// <summary>
/// Flow VLESS так, как его понимает sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №38, 27.10: после правки REALITY та же публичная подписка на
/// 8 683 сервера всё равно роняла туннель — «initialize outbound[8500]:
/// unsupported flow: xtls-rprx-vision-udp443». Один сервер из тысяч, и он
/// единственный: без него движок поднялся со всеми остальными.
/// </para>
/// <para>
/// Что sing-box принимает, проверено `sing-box check` на нашей сборке
/// 1.14.3-extended-2.7.3 (27.10): только пустой flow и <c>xtls-rprx-vision</c>.
/// «unsupported flow» — у <c>xtls-rprx-vision-udp443</c>, у XTLS первой версии
/// (<c>xtls-rprx-direct</c>, <c>-origin</c>, <c>-splice</c>), у <c>none</c>
/// и у самого Vision заглавными или с пробелами.
/// </para>
/// <para>
/// <c>xtls-rprx-vision-udp443</c> — клиентский вариант Vision из Xray: он лишь
/// не глушит у себя UDP на 443, а сервер ждёт обычный Vision. Поэтому он
/// переводится в <c>xtls-rprx-vision</c> (владелец 27.10). На живом сервере
/// не проверено: единственный такой в подписке №38 мёртв — имя не разрешается.
/// Прочее незнакомое не годится (<see cref="ProxyServer.IsSupportedBySingBox"/>):
/// сервер пропускается, а не роняет туннель.
/// </para>
/// </remarks>
public static class VlessFlows
{
    public const string Vision = "xtls-rprx-vision";

    /// <summary>
    /// Flow для sing-box: пустая строка — без flow; <c>null</c> — sing-box такого не знает.
    /// </summary>
    public static string? ForSingBox(string? flow)
    {
        if (string.IsNullOrWhiteSpace(flow))
            return string.Empty;

        return flow.Trim().ToLowerInvariant() is Vision or "xtls-rprx-vision-udp443" ? Vision : null;
    }
}
