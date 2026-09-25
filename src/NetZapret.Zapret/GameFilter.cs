namespace NetZapret.Zapret;

/// <summary>
/// Game filter: десинк начала соединений игр к адресам облаков и хостингов.
/// </summary>
/// <remarks>
/// <para>
/// Перенесён 25.09 с zapret-discord-youtube (Flowseal), где это выключатель
/// в service.bat: пользователь попросил, у него с ним «всё работало быстро».
/// У Flowseal выключатель подставляет в две последние секции каждого .bat
/// порты 1024-65535; выключенный — порт-пустышку, и секции остаются
/// в строке. Здесь выключенный не дописывает ничего: строка запуска
/// побайтно та же, что без этой возможности, — это держит тест.
/// </para>
/// <para>
/// Секции встают последними: берут только то, что не взяла ни одна секция
/// пресета, щит и свои рецепты. Трогают первые четыре пакета соединения
/// (у Flowseal — --dpi-desync-cutoff=n4), дальше игра идёт нетронутой.
/// Отбор по ipset-all — список Flowseal: Cloudflare, облака, хостинги.
/// Игровых сетей Riot в нём нет (проверено 25.09 по пяти диапазонам),
/// но проверять Valorant всё равно живым входом — см. CLAUDE.md.
/// </para>
/// <para>
/// Рецепты — перевод ALT11 на Zapret 2, как в пресете «Flowseal ALT11».
/// Помогают ли они играм, не проверено: выключатель экспериментальный
/// и по умолчанию выключен.
/// </para>
/// </remarks>
public static class GameFilter
{
    /// <summary>Порты игр — как у Flowseal при включённом game filter.</summary>
    public const string Ports = "1024-65535";

    /// <summary>Имена секций: по ним их узнают тесты, журнал и проверка блокировок.</summary>
    public const string TcpName = "NetZapret: игры TCP (game filter)";
    public const string UdpName = "NetZapret: игры UDP (game filter)";

    // Свои имена образцов, с приставкой: пресет мог объявить stun2 или
    // tls_max под теми же именами, а два объявления одного имени движок
    // не обязан переносить молча.
    private const string BlobStun = "nz_game_stun";
    private const string BlobTls = "nz_game_tls";
    private const string BlobUdp = "nz_game_udp";

    private static readonly string[] Globals =
    [
        $"--blob={BlobStun}:@bin/stun2.bin",
        $"--blob={BlobTls}:@bin/tls_clienthello_max_ru.bin",
        $"--blob={BlobUdp}:@bin/ACTIVE_GAME_UDP.bin",
    ];

    /// <summary>Модули, в которых объявлены fake и multisplit.</summary>
    private static readonly string[] Modules = ["zapret-lib.lua", "zapret-antidpi.lua"];

    /// <summary>Дописывает game filter к готовой строке запуска.</summary>
    public static void Apply(List<string> arguments)
    {
        // Глобальные ключи от места в строке не зависят (WinwsCommandLine.SplitGlobals),
        // поэтому идут в самое начало — там их не перепутать с ключами профиля.
        var head = new List<string>();

        foreach (var module in Modules)
        {
            if (!arguments.Any(a => a.StartsWith("--lua-init=", StringComparison.OrdinalIgnoreCase)
                    && a.EndsWith(module, StringComparison.OrdinalIgnoreCase)))
            {
                head.Add($"--lua-init=@lua/{module}");
            }
        }

        head.AddRange(Globals);

        // Перехват шире на порты игр. Без этого WinDivert их не отдаст движку,
        // и секции ниже не увидели бы ни одного пакета.
        Widen(arguments, head, "--wf-tcp-out=");
        Widen(arguments, head, "--wf-udp-out=");

        arguments.InsertRange(0, head);

        arguments.AddRange(
        [
            "--new",
            $"--name={TcpName}",
            $"--filter-tcp={Ports}",
            "--ipset=lists/ipset-all.txt",
            "--ipset-exclude=lists/ipset-exclude.txt",
            "--out-range=-n4",
            "--payload=all",
            $"--lua-desync=fake:blob={BlobStun}:tcp_ts=-1000:repeats=8:payload=all",
            $"--lua-desync=fake:blob={BlobTls}:tcp_ts=-1000:repeats=8:payload=all",
            $"--lua-desync=multisplit:pos=1:seqovl=664:seqovl_pattern={BlobTls}:payload=all",

            "--new",
            $"--name={UdpName}",
            $"--filter-udp={Ports}",
            "--ipset=lists/ipset-all.txt",
            "--ipset-exclude=lists/ipset-exclude.txt",
            "--out-range=-n4",
            "--payload=all",
            $"--lua-desync=fake:blob={BlobUdp}:repeats=10:payload=all",
        ]);
    }

    /// <summary>
    /// Дописывает порты игр к ключу перехвата; нет ключа — заводит его.
    /// </summary>
    private static void Widen(List<string> arguments, List<string> head, string key)
    {
        int index = arguments.FindIndex(a => a.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            head.Add(key + Ports);
            return;
        }

        // Уже покрыто (у V10 — 443-65535) — строку не трогаем.
        if (!Covers(arguments[index][key.Length..]))
            arguments[index] = arguments[index] + "," + Ports;
    }

    /// <summary>Покрывает ли список портов весь диапазон игр 1024-65535.</summary>
    public static bool Covers(string ports)
    {
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-');

            if (bounds.Length == 2
                && int.TryParse(bounds[0], out int low)
                && int.TryParse(bounds[1], out int high)
                && low <= 1024 && high >= 65535)
            {
                return true;
            }
        }

        return false;
    }
}
