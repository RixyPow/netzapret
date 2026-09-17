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
        int clashPort = DefaultClashPort)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            var json = await http.GetStringAsync(
                $"http://127.0.0.1:{clashPort}/proxies/auto", cancellationToken);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("now", out var now))
                return null;

            var name = now.GetString();

            // Селектор может указывать на группу, а не на сервер. Тогда
            // спрашиваем ещё раз у неё — иначе получится «auto-latency»,
            // что не ответ на вопрос «через что мы сейчас ходим».
            if (name is not null && name.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                var inner = await http.GetStringAsync(
                    $"http://127.0.0.1:{clashPort}/proxies/{Uri.EscapeDataString(name)}",
                    cancellationToken);

                using var group = JsonDocument.Parse(inner);

                if (group.RootElement.TryGetProperty("now", out var chosen))
                    return chosen.GetString();
            }

            return name;
        }
        catch (Exception)
        {
            // Движок мог не поднять Clash API либо ещё не успеть. Незнание
            // честнее выдуманного имени сервера.
            return null;
        }
    }

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
        if (string.IsNullOrWhiteSpace(server))
            return TunnelState.Unknown;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            var url = $"http://127.0.0.1:{clashPort}/proxies/{Uri.EscapeDataString(server)}/delay"
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
