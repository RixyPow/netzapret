using NetZapret.Core;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>
/// Куда сейчас идут имена — строкой для вкладки DNS.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №55: при «авто» не было видно, спрашивает ли движок выбранный
/// резолвер напрямую или уже ушёл на запасной или в туннель. Надзор это знает
/// (<see cref="ServiceState.DnsNotice"/>) и пишет на «Главную», только когда
/// что-то не так, — на вкладке DNS строка есть всегда.
/// </para>
/// <para>
/// И второе оттуда же: в «Десинке» без движка ради DNS выбранный резолвер
/// не спрашивается вовсе — имена разрешает Windows своим DNS, — а вкладка
/// этого не говорила (таблица человека: «напрямую» в «Десинке» — провайдер).
/// </para>
/// </remarks>
public static class DnsNow
{
    /// <param name="state">Состояние надзора; <c>null</c> — файла нет.</param>
    /// <param name="engineResolver">
    /// Адрес резолвера, с которым собран работающий движок (<c>remote</c> его
    /// конфига); <c>null</c> — не прочли. Расходится с настройками — выбор ещё
    /// не применён.
    /// </param>
    public static string Describe(AppSettings settings, SupervisorState? state, string? engineResolver)
    {
        if (state is null || !state.IsSupervisorAlive())
            return "Сейчас: движки не запущены — имена разрешает сама Windows.";

        if (state.Services.FirstOrDefault(s => s.Name is "sing-box" or DnsEngine.ServiceName) is not { } engine)
            // Не длиннее двух строк на узком окне: место под строку — ровно две.
            return "Сейчас: имена разрешает сама Windows — выбранный резолвер спрашивается только при «через туннель».";

        if (engine.DnsNotice is { Length: > 0 } notice)
            return "Сейчас: " + notice + ".";

        var resolver = settings.UseRouterDns ? $"роутер {settings.RouterDnsAddress}"
            : settings.DnsCryptActive is { } crypt ? DnsCrypt.Describe(crypt).Title + " через dnscrypt-proxy"
            : DnsSpare.Describe(settings.DnsServer);

        var path = engine.Name == DnsEngine.ServiceName ? "движком, напрямую"
            : settings.DnsVia == DnsRoute.Tunnel ? "через туннель"
            : "напрямую";

        var said = $"Сейчас: {resolver}, {path}.";

        // Выбран другой, а движок ещё со старым — сказать, у кого спрашивают на деле.
        if (!settings.UseRouterDns && settings.DnsCryptActive is null && engineResolver is { Length: > 0 }
            && !string.Equals(engineResolver, settings.DnsServer, StringComparison.OrdinalIgnoreCase))
        {
            said = $"Сейчас: {DnsSpare.Describe(engineResolver)}, {path}; выбранный — после перезапуска движков.";
        }

        return said;
    }
}
