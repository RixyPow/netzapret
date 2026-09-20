using System.IO;
using System.Runtime.InteropServices;
using NetZapret.Core;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Командная строка супервизора: собранная и прочитанная обратно.
/// </summary>
/// <remarks>
/// <para>
/// Проверяется не половина, а шов. <c>BuildArguments</c> отдаёт <b>строку</b>,
/// а <c>Parse</c> принимает <b>массив</b>, и между ними строку расщепляет
/// система — по пробелам, с учётом кавычек. Ошибка живёт ровно там: и пресет
/// «Universal V8», и путь вида <c>C:\Users\Имя\Проекты</c> содержат пробелы
/// и кириллицу, и обе беды этого рода уже случались.
/// </para>
/// <para>
/// Окно запускает супервизор этой самой строкой, поэтому расхождение здесь
/// означает движки, поднятые не с тем пресетом или не с тем конфигом, —
/// и тишину вместо жалобы, потому что при автозапуске окна нет.
/// </para>
/// </remarks>
public sealed class SupervisorArgumentsTests
{
    /// <summary>
    /// Расщепляет строку так же, как это сделает Windows при запуске.
    /// </summary>
    /// <remarks>
    /// Через <c>CommandLineToArgvW</c> — ту самую функцию, которой
    /// пользуется сама система. Свой разборщик кавычек здесь был бы
    /// третьей реализацией правил, и проверял бы он себя.
    /// </remarks>
    private static string[] SplitLikeWindows(string commandLine)
    {
        var handle = CommandLineToArgvW("nz.exe " + commandLine, out int count);

        Assert.NotEqual(IntPtr.Zero, handle);

        try
        {
            var parts = new string[count];

            for (int i = 0; i < count; i++)
                parts[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(handle, i * IntPtr.Size))!;

            // Нулевой — имя программы, его супервизору не передают.
            return parts[1..];
        }
        finally
        {
            LocalFree(handle);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static SupervisorHost.Options RoundTrip(AppSettings settings) =>
        SupervisorHost.Parse(SplitLikeWindows(SupervisorHost.BuildArguments(settings)));

    /// <summary>
    /// Имя пресета с пробелом доезжает целиком.
    /// </summary>
    /// <remarks>
    /// Пресет по умолчанию зовётся «Universal V8», то есть пробел здесь —
    /// не редкий случай, а обычный. Потеряй его кавычки — супервизор получил бы
    /// «Universal», не нашёл такого пресета и поднялся бы без десинка. Сверху
    /// это выглядит как работающая программа, у которой просто ничего
    /// не открывается.
    /// </remarks>
    [Fact]
    public void A_preset_name_with_a_space_survives()
    {
        var read = RoundTrip(new AppSettings { PresetName = "Universal V8" });

        Assert.Equal("Universal V8", read.Preset);
    }

    /// <summary>Путь с пробелами и кириллицей — тоже.</summary>
    /// <remarks>
    /// У владельца программа лежит в <c>C:\Users\rogfa\Projects\netzapret</c>,
    /// но у кого-то она окажется в «Мои документы», и кириллица в пути уже
    /// однажды ломала разбор аргументов — тогда у Cygwin внутри winws2.
    /// </remarks>
    [Fact]
    public void A_path_with_spaces_and_cyrillic_survives()
    {
        var settings = new AppSettings
        {
            ProxyConfigPath = Path.Combine("Мои документы", "конфиг движка", "singbox.json"),
        };

        var read = RoundTrip(settings);

        Assert.EndsWith(Path.Combine("Мои документы", "конфиг движка", "singbox.json"), read.ProxyConfig);
        Assert.True(Path.IsPathFullyQualified(read.ProxyConfig));
    }

    /// <summary>
    /// Выключенный десинк не передаёт пресета вовсе.
    /// </summary>
    /// <remarks>
    /// Передай его при выключенном — супервизор поднял бы winws2 там,
    /// где человек его выключил, и обход делал бы то, чего не просили.
    /// </remarks>
    [Fact]
    public void Desync_turned_off_sends_no_preset()
    {
        var read = RoundTrip(new AppSettings { Mode = OperatingMode.Off });

        Assert.Null(read.Preset);
    }

    /// <summary>Без туннеля уходит <c>--no-proxy</c>, а не путь к конфигу.</summary>
    /// <remarks>
    /// Разница не косметическая: с путём супервизор поднял бы sing-box
    /// и забрал TUN, а человек просил обойтись десинком.
    /// </remarks>
    [Fact]
    public void Without_a_tunnel_no_proxy_is_sent()
    {
        var read = RoundTrip(new AppSettings { Mode = OperatingMode.DesyncOnly });

        Assert.True(read.NoProxy);
    }

    /// <summary>
    /// Проверка прохода трафика передаётся ровно по настройке.
    /// </summary>
    /// <remarks>
    /// Тот же флаг решает, поднимется ли служебный вход в конфиге движка.
    /// Разойдись эти два решения — проверка вечно стучалась бы туда,
    /// где никого нет.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Traffic_verification_follows_the_setting(bool asked)
    {
        Assert.Equal(asked, RoundTrip(new AppSettings { VerifyTraffic = asked }).VerifyTraffic);
    }

    /// <summary>Журнал: путь передаётся, когда журналы включены.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_log_path_follows_the_setting(bool enabled)
    {
        var read = RoundTrip(new AppSettings { LogsEnabled = enabled });

        Assert.Equal(enabled, read.LogPath is { Length: > 0 });
    }

    /// <summary>
    /// Обычная настройка доезжает целиком, всеми полями разом.
    /// </summary>
    /// <remarks>
    /// Отдельные проверки выше ловят по одному полю; эта — что они
    /// не мешают друг другу, когда стоят в одной строке.
    /// </remarks>
    [Fact]
    public void A_whole_realistic_setting_survives_the_round_trip()
    {
        var settings = new AppSettings
        {
            PresetName = "Universal V8",
            ProxyConfigPath = Path.Combine("runtime", "singbox.json"),
            VerifyTraffic = true,
            LogsEnabled = true,
        };

        var read = RoundTrip(settings);

        Assert.Equal("Universal V8", read.Preset);
        Assert.True(read.VerifyTraffic);
        Assert.False(read.NoProxy);
        Assert.EndsWith("singbox.json", read.ProxyConfig);
        Assert.EndsWith("supervisor.log", read.LogPath);
    }
}
