using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetZapret.Proxy;

/// <summary>Условие правила kill switch.</summary>
public abstract record KillSwitchCondition
{
    /// <summary>Соединение открывает эта программа (путь к exe).</summary>
    public sealed record App(string Path) : KillSwitchCondition;

    /// <summary>Свой адрес соединения — адрес TUN: соединение идёт в туннель.</summary>
    public sealed record LocalAddress(IPAddress Address, int Prefix) : KillSwitchCondition;

    /// <summary>Адрес назначения в сети.</summary>
    public sealed record RemoteAddress(IPAddress Address, int Prefix) : KillSwitchCondition;

    /// <summary>Порт назначения.</summary>
    public sealed record RemotePort(ushort Port) : KillSwitchCondition;

    /// <summary>Соединение внутри машины (loopback).</summary>
    public sealed record Loopback : KillSwitchCondition;
}

/// <summary>Одно правило: пропустить или заблокировать, с весом внутри своего подслоя.</summary>
/// <param name="Weight">0–15: в подслое решает совпавшее правило с наибольшим весом.</param>
/// <param name="Condition"><c>null</c> — подходит под любое соединение.</param>
public sealed record KillSwitchRule(string Name, bool V6, bool Permit, byte Weight, KillSwitchCondition? Condition);

/// <summary>
/// Kill switch: пока объект жив, трафик мимо туннеля не выходит из машины.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 27.10: жёсткий kill switch, только в режиме «Туннель»
/// (<see cref="NetZapret.Core.AppSettings.KillSwitch"/>). Ставится фильтрами
/// Windows Filtering Platform на слое разрешения исходящих соединений (ALE
/// connect): блокируется всё, кроме соединений в TUN (свой адрес — адрес TUN),
/// самого движка туннеля и NetZapret, loopback и локальной сети.
/// </para>
/// <para>
/// Сессия WFP — динамическая: все фильтры Windows снимает сама, когда закрыт
/// её дескриптор или умер процесс. Держит её надзор, а он живёт ровно столько,
/// сколько движки: падение или перезапуск sing-box фильтры переживают (на это
/// время сети нет — в этом и смысл), остановка движков или падение надзора
/// их снимает, и сеть не остаётся запертой навсегда. Ничего постоянного
/// в правилах брандмауэра не пишется.
/// </para>
/// <para>
/// DNS (порт 53) к локальной сети блокируется отдельно и выше её разрешения:
/// иначе с умершим TUN Windows спросила бы роутер, и имена ушли бы провайдеру.
/// </para>
/// </remarks>
public sealed class KillSwitch : IDisposable
{
    private IntPtr _engine;

    private KillSwitch(IntPtr engine) => _engine = engine;

    /// <summary>Сколько правил поставлено.</summary>
    public int RuleCount { get; private init; }

    /// <summary>
    /// Правила kill switch: что пропустить и что заблокировать.
    /// </summary>
    /// <param name="apps">Программы, которым можно мимо туннеля: движок и сам NetZapret.</param>
    /// <param name="tunV4">Адрес TUN по IPv4.</param>
    /// <param name="tunV6">Адрес TUN по IPv6; <c>null</c> — IPv6 у TUN нет.</param>
    public static IReadOnlyList<KillSwitchRule> Plan(IEnumerable<string> apps, IPAddress tunV4, IPAddress? tunV6)
    {
        var rules = new List<KillSwitchRule>();

        foreach (bool v6 in new[] { false, true })
        {
            // Блокировать IPv6 нужно и без IPv6 у TUN: в «Туннеле» IPv6 тогда
            // в туннель не идёт вовсе, а наружу — мимо него.
            foreach (var app in apps)
                rules.Add(new("программа " + Path.GetFileName(app), v6, true, 15, new KillSwitchCondition.App(app)));

            if (!v6)
                rules.Add(new("в туннель", false, true, 14, new KillSwitchCondition.LocalAddress(tunV4, 32)));
            else if (tunV6 is not null)
                rules.Add(new("в туннель", true, true, 14, new KillSwitchCondition.LocalAddress(tunV6, 128)));

            rules.Add(new("loopback", v6, true, 13, new KillSwitchCondition.Loopback()));
            rules.Add(new("DNS мимо туннеля", v6, false, 12, new KillSwitchCondition.RemotePort(53)));

            foreach (var (address, prefix) in v6 ? LocalNetworksV6 : LocalNetworksV4)
            {
                rules.Add(new($"локальная сеть {address}/{prefix}", v6, true, 10,
                    new KillSwitchCondition.RemoteAddress(IPAddress.Parse(address), prefix)));
            }

            rules.Add(new("всё прочее", v6, false, 0, null));
        }

        return rules;
    }

    /// <summary>Локальные сети: роутер, принтер, DHCP, широковещание и многоадресная рассылка.</summary>
    private static readonly (string, int)[] LocalNetworksV4 =
    [
        ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16),
        ("224.0.0.0", 4), ("255.255.255.255", 32),
    ];

    private static readonly (string, int)[] LocalNetworksV6 =
    [
        ("fe80::", 10), ("ff00::", 8),
    ];

    /// <summary>Ставит правила; не вышло — исключение, и не стоит ни одно.</summary>
    /// <exception cref="InvalidOperationException">WFP отказал; в тексте — шаг и код.</exception>
    public static KillSwitch Engage(IReadOnlyList<KillSwitchRule> rules) => Apply(rules, commit: true);

    /// <summary>
    /// Пробный прогон: те же вызовы WFP, но транзакция отменяется — сеть не трогается.
    /// </summary>
    /// <remarks>
    /// Для тестов: проверяет, что Windows принимает наши структуры. Нужны права
    /// администратора; без них WFP отказывает уже на начале транзакции.
    /// </remarks>
    internal static void DryRun(IReadOnlyList<KillSwitchRule> rules)
    {
        using var _ = Apply(rules, commit: false);
    }

    private static KillSwitch Apply(IReadOnlyList<KillSwitchRule> rules, bool commit)
    {
        using var memory = new Unmanaged();

        var session = new Native.Session
        {
            Key = Guid.NewGuid(),
            Display = memory.Display("NetZapret kill switch", "Блокирует трафик мимо туннеля, пока работают движки NetZapret."),
            Flags = Native.SessionFlagDynamic,
        };

        Check(Native.FwpmEngineOpen0(IntPtr.Zero, Native.RpcAuthnWinNt, IntPtr.Zero, ref session, out var engine), "открыть WFP");

        var killSwitch = new KillSwitch(engine) { RuleCount = rules.Count };

        try
        {
            Check(Native.FwpmTransactionBegin0(engine, 0), "начать транзакцию");

            var subLayer = new Native.SubLayer
            {
                Key = Guid.NewGuid(),
                Display = memory.Display("NetZapret kill switch", "Правила kill switch NetZapret."),
                Weight = 0xFFFF,
            };

            Check(Native.FwpmSubLayerAdd0(engine, ref subLayer, IntPtr.Zero), "добавить подслой");

            foreach (var rule in rules)
            {
                var filter = new Native.Filter
                {
                    Key = Guid.NewGuid(),
                    Display = memory.Display("NetZapret kill switch: " + rule.Name, null),
                    LayerKey = rule.V6 ? Native.LayerConnectV6 : Native.LayerConnectV4,
                    SubLayerKey = subLayer.Key,
                    Weight = new Native.Value { Type = Native.TypeUInt8, Raw = rule.Weight },
                    Action = new Native.Action { Type = rule.Permit ? Native.ActionPermit : Native.ActionBlock },
                };

                if (rule.Condition is { } condition)
                {
                    filter.NumFilterConditions = 1;
                    filter.FilterCondition = memory.Condition(condition, rule.V6);
                }

                Check(Native.FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _), "добавить правило «" + rule.Name + "»");
            }

            if (commit)
                Check(Native.FwpmTransactionCommit0(engine), "применить правила");
            else
                Check(Native.FwpmTransactionAbort0(engine), "отменить транзакцию");
        }
        catch
        {
            // Динамическая сессия: закрыть её — значит снять всё добавленное.
            killSwitch.Dispose();
            throw;
        }

        return killSwitch;
    }

    /// <summary>Снимает правила: закрывает динамическую сессию.</summary>
    public void Dispose()
    {
        var engine = Interlocked.Exchange(ref _engine, IntPtr.Zero);

        if (engine != IntPtr.Zero)
            Native.FwpmEngineClose0(engine);
    }

    private static void Check(uint code, string step)
    {
        if (code != 0)
            throw new InvalidOperationException($"kill switch: не удалось {step}, код 0x{code:X8}");
    }

    /// <summary>Неуправляемая память под структуры WFP; освобождается после транзакции.</summary>
    private sealed class Unmanaged : IDisposable
    {
        private readonly List<IntPtr> _blocks = [];
        private readonly List<IntPtr> _appIds = [];

        public Native.DisplayData Display(string name, string? description) => new()
        {
            Name = Keep(Marshal.StringToHGlobalUni(name)),
            Description = description is null ? IntPtr.Zero : Keep(Marshal.StringToHGlobalUni(description)),
        };

        public IntPtr Condition(KillSwitchCondition condition, bool v6)
        {
            var native = new Native.FilterCondition();

            switch (condition)
            {
                case KillSwitchCondition.App app:
                    Check(Native.FwpmGetAppIdFromFileName0(app.Path, out var blob), "найти программу " + app.Path);
                    _appIds.Add(blob);
                    native.FieldKey = Native.ConditionAppId;
                    native.MatchType = Native.MatchEqual;
                    native.Value = new Native.Value { Type = Native.TypeByteBlob, Pointer = blob };
                    break;

                case KillSwitchCondition.LocalAddress local:
                    native.FieldKey = Native.ConditionLocalAddress;
                    native.MatchType = Native.MatchEqual;
                    native.Value = Address(local.Address, local.Prefix, v6);
                    break;

                case KillSwitchCondition.RemoteAddress remote:
                    native.FieldKey = Native.ConditionRemoteAddress;
                    native.MatchType = Native.MatchEqual;
                    native.Value = Address(remote.Address, remote.Prefix, v6);
                    break;

                case KillSwitchCondition.RemotePort port:
                    native.FieldKey = Native.ConditionRemotePort;
                    native.MatchType = Native.MatchEqual;
                    native.Value = new Native.Value { Type = Native.TypeUInt16, Raw = port.Port };
                    break;

                case KillSwitchCondition.Loopback:
                    native.FieldKey = Native.ConditionFlags;
                    native.MatchType = Native.MatchFlagsAllSet;
                    native.Value = new Native.Value { Type = Native.TypeUInt32, Raw = Native.FlagIsLoopback };
                    break;

                default:
                    throw new NotSupportedException(condition.GetType().Name);
            }

            var pointer = Keep(Marshal.AllocHGlobal(Marshal.SizeOf<Native.FilterCondition>()));
            Marshal.StructureToPtr(native, pointer, false);
            return pointer;
        }

        private Native.Value Address(IPAddress address, int prefix, bool v6)
        {
            var bytes = address.GetAddressBytes();

            if (!v6)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("адрес IPv6 в правиле IPv4: " + address);

                // WFP ждёт адрес IPv4 и маску в порядке байт машины, а не сети.
                uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

                var pointer = Keep(Marshal.AllocHGlobal(8));
                Marshal.WriteInt32(pointer, 0, unchecked((int)value));
                Marshal.WriteInt32(pointer, 4, unchecked((int)mask));
                return new Native.Value { Type = Native.TypeV4AddrMask, Pointer = pointer };
            }

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("адрес IPv4 в правиле IPv6: " + address);

            // FWP_V6_ADDR_AND_MASK: 16 байт адреса в сетевом порядке и длина префикса.
            var block = Keep(Marshal.AllocHGlobal(17));
            Marshal.Copy(bytes, 0, block, 16);
            Marshal.WriteByte(block, 16, (byte)prefix);
            return new Native.Value { Type = Native.TypeV6AddrMask, Pointer = block };
        }

        private IntPtr Keep(IntPtr pointer)
        {
            _blocks.Add(pointer);
            return pointer;
        }

        public void Dispose()
        {
            foreach (var block in _blocks)
                Marshal.FreeHGlobal(block);

            for (int i = 0; i < _appIds.Count; i++)
            {
                var blob = _appIds[i];
                Native.FwpmFreeMemory0(ref blob);
            }
        }
    }

    /// <summary>Объявления WFP — по заголовкам Windows SDK (fwpmu.h, fwpmtypes.h, fwptypes.h).</summary>
    internal static class Native
    {
        public static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        public static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
        public static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
        public static readonly Guid ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
        public static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
        public static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
        public static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");

        public const uint SessionFlagDynamic = 0x1;
        public const uint RpcAuthnWinNt = 10;
        public const uint ActionBlock = 0x1001;
        public const uint ActionPermit = 0x1002;
        public const uint FlagIsLoopback = 0x1;

        public const uint TypeUInt8 = 1;
        public const uint TypeUInt16 = 2;
        public const uint TypeUInt32 = 3;
        public const uint TypeByteBlob = 12;
        public const uint TypeV4AddrMask = 0x100;
        public const uint TypeV6AddrMask = 0x101;

        public const uint MatchEqual = 0;
        public const uint MatchFlagsAllSet = 6;

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayData
        {
            public IntPtr Name;
            public IntPtr Description;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ByteBlob
        {
            public uint Size;
            public IntPtr Data;
        }

        /// <summary>FWP_VALUE0 и FWP_CONDITION_VALUE0: тип и объединение в 8 байт.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct Value
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ulong Raw;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Session
        {
            public Guid Key;
            public DisplayData Display;
            public uint Flags;
            public uint TxnWaitTimeoutMs;
            public uint ProcessId;
            public IntPtr Sid;
            public IntPtr UserName;
            public int KernelMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SubLayer
        {
            public Guid Key;
            public DisplayData Display;
            public uint Flags;
            public IntPtr ProviderKey;
            public ByteBlob ProviderData;
            public ushort Weight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FilterCondition
        {
            public Guid FieldKey;
            public uint MatchType;
            public Value Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Action
        {
            public uint Type;
            public Guid FilterType;
        }

        /// <summary>Объединение rawContext / providerContextKey.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct Context
        {
            [FieldOffset(0)] public ulong Raw;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Filter
        {
            public Guid Key;
            public DisplayData Display;
            public uint Flags;
            public IntPtr ProviderKey;
            public ByteBlob ProviderData;
            public Guid LayerKey;
            public Guid SubLayerKey;
            public Value Weight;
            public uint NumFilterConditions;
            public IntPtr FilterCondition;
            public Action Action;
            public Context Context;
            public IntPtr Reserved;
            public ulong FilterId;
            public Value EffectiveWeight;
        }

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, ref Session session, out IntPtr engine);

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmEngineClose0(IntPtr engine);

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmTransactionCommit0(IntPtr engine);

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmTransactionAbort0(IntPtr engine);

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmSubLayerAdd0(IntPtr engine, ref SubLayer subLayer, IntPtr securityDescriptor);

        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr securityDescriptor, out ulong id);

        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
        public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

        [DllImport("fwpuclnt.dll")]
        public static extern void FwpmFreeMemory0(ref IntPtr pointer);
    }
}
