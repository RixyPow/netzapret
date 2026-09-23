using System.Net;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Обстановка, в которой снят замер: через что ходим и куда выходим.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт без этого нечем сравнить с другим отчётом. Проверка снимает
/// не свойства сети, а поведение сети под конкретной настройкой, и два
/// прогона под разными пресетами дают разные строки — при том что оба
/// выглядят одинаково достоверно.
/// </para>
/// <para>
/// Лежит в библиотеке, а не во фронте, и это не вкусовщина. До 17.09 всё это
/// было приватными методами консольной команды, а окно не имело ничего —
/// ни состояния туннеля, ни страны выхода. Раз консоль решено убрать,
/// переносить её приватные методы в окно значило бы завести вторую копию
/// там же, где первая как раз и умирает. Общее место — единственный способ
/// не дать им разойтись.
/// </para>
/// </remarks>
public static class TunnelStatus
{
    /// <summary>Порт, на котором движок держит Clash API.</summary>
    public const int DefaultClashPort = 9090;

    /// <summary>
    /// Через какой сервер движок ходит прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Спрашивается у самого движка, а не берётся из настроек: в настройках
    /// стоит «авто», а работает в этот момент один определённый, и назвать
    /// надо его.
    /// </remarks>
    public static async Task<string?> CurrentServerAsync(
        CancellationToken cancellationToken,
        int clashPort = DefaultClashPort) =>
        (await CurrentExitAsync(cancellationToken, clashPort)).Server;

    /// <summary>
    /// Сервер прямо сейчас и выбран ли он автоподбором, а не закреплён.
    /// </summary>
    /// <remarks>
    /// Второе важно не меньше первого. 23.09 в настройках стояло «авто»,
    /// а движок держался WARP из своего кэша, и Telegram висел; заметить это
    /// можно было, только спросив движок, — теперь это видно в <c>nz status</c>.
    /// </remarks>
    public static async Task<(string? Server, bool Automatic)> CurrentExitAsync(
        CancellationToken cancellationToken,
        int clashPort = DefaultClashPort)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            var json = await http.GetStringAsync(
                $"http://127.0.0.1:{clashPort}/proxies/auto", cancellationToken);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("now", out var now))
                return (null, false);

            var name = Named(now);

            // Селектор может указывать на группу, а не на сервер. Тогда
            // спрашиваем ещё раз у неё — иначе получится «auto-latency»,
            // что не ответ на вопрос «через что мы сейчас ходим».
            if (name is not null && name.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                var inner = await http.GetStringAsync(
                    $"http://127.0.0.1:{clashPort}/proxies/{Uri.EscapeDataString(name)}",
                    cancellationToken);

                using var group = JsonDocument.Parse(inner);

                // Пустой ответ означает, что группа ещё не выбрала: она
                // из тех, что меряют задержку, и до первого замера ей выбирать
                // не из чего. Возвращаем имя самой группы, а не пустоту —
                // по нему хотя бы можно спросить состояние.
                return (group.RootElement.TryGetProperty("now", out var chosen)
                    ? Named(chosen) ?? name
                    : name, true);
            }

            return (name, false);
        }
        catch (Exception)
        {
            // Движок мог не поднять Clash API либо ещё не успеть. Незнание
            // честнее выдуманного имени сервера.
            return (null, false);
        }
    }

    /// <summary>
    /// Имя из ответа движка; пустая строка считается отсутствием имени.
    /// </summary>
    /// <remarks>
    /// Разница видна в отчёте. Группа, меряющая задержку, до первого замера
    /// отвечает пустым <c>now</c>, а не отсутствующим, — и проверка, начатая
    /// сразу после запуска движков, писала в шапку «Сервер: » с пустотой
    /// после двоеточия и «Туннель: состояние не выяснено». Замер 17.09 17:44:
    /// движки подняты в 17:43:28, отчёт снят через полминуты, а в 17:46 та же
    /// группа уже называла «Германия — TLS XHTTP».
    /// </remarks>
    private static string? Named(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } name
            && !string.IsNullOrWhiteSpace(name)
                ? name
                : null;

    /// <summary>
    /// Проходит ли трафик через названный выход.
    /// </summary>
    /// <remarks>
    /// Спрашивается у самого движка его же диагностикой: он отвечает 200
    /// с задержкой, когда проба дошла, и 5xx, когда не дошла. Разбирать тело
    /// незачем — сам факт неуспеха и означает, что через этот выход трафик
    /// не идёт.
    /// </remarks>
    public static async Task<TunnelState> StateAsync(
        string? server,
        CancellationToken cancellationToken,
        int clashPort = DefaultClashPort)
    {
        // Имени нет — спрашиваем селектор. Он есть всегда, пока движок жив,
        // и запрос задержки к нему заставляет группу выбрать. Прежде здесь
        // возвращалось «состояние не выяснено», и отчёт, снятый сразу после
        // запуска движков, молчал о туннеле при живом туннеле.
        var target = string.IsNullOrWhiteSpace(server) ? "auto" : server;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            var url = $"http://127.0.0.1:{clashPort}/proxies/{Uri.EscapeDataString(target)}/delay"
                + "?timeout=5000&url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204");

            using var response = await http.GetAsync(url, cancellationToken);

            return response.IsSuccessStatusCode ? TunnelState.Alive : TunnelState.Dead;
        }
        catch (Exception)
        {
            // Движок мог не поднять Clash API. Это незнание, а не приговор:
            // объявить туннель мёртвым по недоступности его же диагностики
            // значило бы обвинить исправную трубу.
            return TunnelState.Unknown;
        }
    }

    /// <summary>Имя, по которому проверяется внешний адрес.</summary>
    private const string ExitProbe = "api.ipify.org";

    /// <summary>
    /// Внешний адрес, страна и то, через что всё это получено.
    /// </summary>
    /// <remarks>
    /// Третье не менее важно первых двух. С поднятым туннелем ответ службы
    /// определения адреса может прийти и по домашнему каналу, и тогда
    /// «страна выхода» описывает провайдера, а не сервер подписки.
    /// </remarks>
    public static async Task<ExitReading> ReadExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

            var address = (await http.GetStringAsync($"https://{ExitProbe}", cancellationToken)).Trim();
            var country = (await http.GetStringAsync("https://ipinfo.io/country", cancellationToken)).Trim();

            return new ExitReading
            {
                Address = address,
                Country = country,
                Tunnelled = await WentThroughTunnelAsync(ExitProbe, cancellationToken),
            };
        }
        catch (Exception)
        {
            // Не выяснилось — молчим. Отсутствие сведений хуже ложных,
            // но ложные здесь стоили бы смены рабочего сервера ни за чем.
            return new ExitReading { Tunnelled = false };
        }
    }

    /// <summary>Разрешается ли имя в fakeip — то есть уйдёт ли оно в туннель.</summary>
    private static async Task<bool> WentThroughTunnelAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

            return addresses.Any(a => TunnelHealth.IsFakeIp(a));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Путь к движку sing-box рядом с программой.
    /// </summary>
    /// <remarks>
    /// Нужен тем проверкам, которые поднимают свой экземпляр движка вместо
    /// того, чтобы трогать работающий, — см. <see cref="TunnelReach"/>.
    /// Подъём по каталогам нужен только разработчику: в поставке первая же
    /// проверка попадает в цель.
    /// </remarks>
    public static string? FindSingBox()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

        if (File.Exists(beside))
            return beside;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            try
            {
                var candidate = Directory
                    .GetFiles(directory.FullName, "sing-box.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (candidate is not null)
                    return candidate;
            }
            catch (Exception)
            {
                // Каталог без доступа — не повод прекращать поиск выше.
            }

            directory = directory.Parent;
        }

        return null;
    }
}
