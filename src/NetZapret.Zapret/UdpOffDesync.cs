using System.Text;
using NetZapret.Core.Rules;

namespace NetZapret.Zapret;

/// <summary>
/// Сети, UDP к которым winws2 не перехватывает вовсе: игровой UDP Riot,
/// пока его часть не поставлена на «десинк».
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №11 и жалоба из Telegram (30.09): Valorant на «десинке»
/// не запускается, на «напрямую» пинг 500+. «Напрямую» у Riot — щит
/// по именам (<c>WinwsCommandLine.Shield</c>), а игровой UDP идёт к сетям
/// Riot по голым адресам, имени в нём нет. Такие пакеты WinDivert снимал
/// с сети при любом маршруте Riot, а при game filter ещё и подделывал:
/// все сети Riot лежат в ipset-all. Владелец 30.09: разделить Riot
/// на «Вход и клиент» и «Игровой UDP» и вывести UDP из-под десинка.
/// </para>
/// <para>
/// Вывести можно только из самого перехвата (<see cref="TunnelCapture"/>):
/// профиль <c>pass</c> тут не помощник, до него пакет уже снят с сети.
/// </para>
/// <para>
/// Решение — по маршруту части «Игровой UDP», тем же вопросом движку,
/// каким её показывают «Маршруты» (<see cref="ServiceRouting"/>). Всё, кроме
/// «десинка», выводит её из перехвата: «напрямую» — по смыслу, а «через VPN»
/// уводит её в туннель, где десинк ей тоже ни к чему. По умолчанию часть
/// «напрямую» — базовым правилом в <c>config/rules.yaml</c>.
/// </para>
/// <para>
/// Передаётся файлом, как свои рецепты (<see cref="OwnDesyncLists"/>):
/// правила читает окно, строку запуска winws2 собирает супервизор.
/// </para>
/// </remarks>
public static class UdpOffDesync
{
    public static string DefaultPath => Path.Combine("runtime", "desync-udp-off.txt");

    /// <summary>Сети, UDP к которым десинку не отдаётся, по нынешним правилам.</summary>
    public static IReadOnlyList<string> Choose(RuleEngine engine, string? zapretRoot)
    {
        var networks = AddressListReader.Expand([ProgramCapture.RiotNetwork], zapretRoot, out _);

        if (networks.Count == 0)
            return [];

        return ServiceRouting.AddressMode(networks[0], engine) == RoutingMode.Desync ? [] : networks;
    }

    /// <summary>
    /// Записывает сети; пустой список тоже пишется.
    /// </summary>
    /// <remarks>
    /// Иначе вчерашний файл продолжал бы выводить Riot из перехвата после того,
    /// как человек поставил её UDP на «десинк».
    /// </remarks>
    public static void Write(IReadOnlyList<string> networks, string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(target, networks, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Не записалось — winws2 перехватит UDP Riot, как до 30.09.
        }
    }

    public static IReadOnlyList<string> Read(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            return File.Exists(target)
                ? File.ReadAllLines(target).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
