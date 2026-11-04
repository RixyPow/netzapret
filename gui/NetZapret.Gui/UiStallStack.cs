using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Runtime;

namespace NetZapret.Gui;

/// <summary>
/// Где стоит поток окна — стек, снятый в момент подвисания.
/// </summary>
/// <remarks>
/// <para>
/// Журнал владельца 05.11: 414 подвисаний, из них больше всего «окно ждало —
/// не процессор»: «Главная» до 12 с сразу после подъёма движков, «VPN»
/// по 4,5–5,4 с ровно в те минуты, когда туннель не пропускал трафик,
/// «TG Proxy» до 17 с. Поток окна стоит на синхронном ожидании, а где —
/// по длительности и разделу не сказать, и грепом очевидного не нашлось.
/// </para>
/// <para>
/// Свой поток окно прочитать не может, пока тот стоит, — читает снимок
/// процесса (ClrMD, <c>CreateSnapshotAndAttach</c>: клон памяти Windows,
/// окно при этом не останавливается). Дорого — сотни миллисекунд, — поэтому
/// только на подвисании дольше секунды и не чаще раза в минуту.
/// </para>
/// </remarks>
internal static class UiStallStack
{
    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    private static DateTime _last = DateTime.MinValue;

    /// <summary>Сверху — сколько кадров брать как есть: там и видно, что именно ждут.</summary>
    private const int Top = 4;

    /// <summary>Своих кадров ниже — сколько: по ним видно, кто ждёт.</summary>
    private const int Own = 6;

    /// <summary>
    /// Стек потока окна одной строкой; <c>null</c> — снимать рано (пауза между снимками).
    /// </summary>
    public static string? Capture(int managedThreadId)
    {
        var now = DateTime.UtcNow;

        if (now - _last < Pause)
            return null;

        _last = now;

        return Snapshot(managedThreadId);
    }

    /// <summary>Снимок без паузы между снимками — для <see cref="Capture"/> и тестов.</summary>
    internal static string Snapshot(int managedThreadId)
    {
        // Библиотека чтения стеков (DAC) — только та, что лежит рядом с .NET. Не нашла
        // своей — ClrMD сама качает её с сервера символов Microsoft, а запроса в сеть
        // на подвисании никто не ждёт. В выпуске (единый файл) её нет: проверено
        // по архиву 1.3.1 — в NetZapret.exe mscordaccore.dll не вложен. Стек там
        // не снимется, и сторож так и скажет; у сборки build.cmd DAC лежит рядом.
        var dac = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "mscordaccore.dll");

        if (!File.Exists(dac))
            return "стек не снят: нет библиотеки чтения стеков рядом с .NET";

        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            target.FileLocator = null;
            using var runtime = target.ClrVersions[0].CreateRuntime(dac);

            var thread = runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == managedThreadId);

            if (thread is null)
                return "стек не снят: поток окна не найден";

            var frames = thread.EnumerateStackTrace()
                .Where(f => f.Method is not null)
                .Select(f => Name(f.Method!.Type?.Name, f.Method.Name))
                .ToList();

            return frames.Count == 0 ? "стек не снят: кадров нет" : "стоит в: " + Line(frames);
        }
        catch (Exception ex)
        {
            // Снимок — подсказка, а не работа окна: не вышло — так и сказать.
            return "стек не снят: " + ex.GetType().Name;
        }
    }

    /// <summary>
    /// Верхние кадры подряд, ниже — только наши: что ждут и кто.
    /// </summary>
    internal static string Line(IReadOnlyList<string> frames)
    {
        var picked = frames.Take(Top).ToList();

        foreach (var frame in frames.Skip(Top))
        {
            if (picked.Count(IsOurs) >= Own)
                break;

            if (IsOurs(frame) && picked[^1] != frame)
                picked.Add(frame);
        }

        return string.Join(" ← ", picked);
    }

    private static bool IsOurs(string frame) =>
        !frame.StartsWith("System.", StringComparison.Ordinal) && !frame.StartsWith("MS.", StringComparison.Ordinal)
        && !frame.StartsWith("Microsoft.", StringComparison.Ordinal);

    /// <summary>
    /// Кадр коротко: без пространства имён своего кода, async — по имени метода.
    /// </summary>
    /// <remarks>
    /// Асинхронный метод в стеке — <c>VpnView+&lt;RefreshAsync&gt;d__45.MoveNext</c>,
    /// лямбда — <c>VpnView+&lt;&gt;c__DisplayClass12_0.&lt;Show&gt;b__0</c>;
    /// человеку и мне нужно «VpnView.RefreshAsync».
    /// </remarks>
    internal static string Name(string? type, string? method)
    {
        var owner = type ?? "?";

        foreach (var prefix in new[] { "NetZapret.Gui.", "NetZapret." })
        {
            if (owner.StartsWith(prefix, StringComparison.Ordinal))
            {
                owner = owner[prefix.Length..];
                break;
            }
        }

        var call = method ?? "?";

        var async = Regex.Match(owner, @"^(?<outer>[^+]+)\+<(?<name>[^>]+)>d__\d+$");

        if (async.Success && call == "MoveNext")
            return $"{async.Groups["outer"].Value}.{async.Groups["name"].Value}";

        var lambda = Regex.Match(call, @"^<(?<name>[^>]+)>b__");

        if (lambda.Success)
            return $"{Regex.Replace(owner, @"\+<>c(__DisplayClass[\d_]+)?$", string.Empty)}.{lambda.Groups["name"].Value} (лямбда)";

        return $"{owner.Replace('+', '.')}.{call}";
    }
}
