using System.Diagnostics;
using NetZapret.Core;
using NetZapret.Core.Updates;

namespace NetZapret.Gui;

/// <summary>
/// Поиск обновления при запуске программы и, если человек выбрал
/// автообновление, установка без вопроса (12.11).
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 12.11: ставить само — только при запуске, когда человек
/// ещё ничем не занят. Найденное кнопкой «Проверить» и всё, что не успело
/// скачаться за <see cref="InstallWithin"/>, предлагается как прежде —
/// окном обновления.
/// </para>
/// <para>
/// Раз за процесс и из приложения, а не из окна: при автозапуске окна нет,
/// а проверка до 12.11 жила в окне — запущенная в трей программа узнавала
/// об обновлении, только когда её открывали.
/// </para>
/// </remarks>
internal static class StartupUpdate
{
    /// <summary>
    /// Сколько от запуска может идти закачка, чтобы подмена ещё была «при запуске».
    /// </summary>
    /// <remarks>
    /// Архив ~120 МБ: через обход это минута-две, голой сетью у части людей —
    /// десятки минут (Максим, 04.10: 2 % за 5–10 минут). Позже человек уже
    /// занят, и гасить ему обход без спроса нельзя. Срок — мой выбор.
    /// </remarks>
    public static readonly TimeSpan InstallWithin = TimeSpan.FromMinutes(10);

    /// <summary>Идёт проверка или установка при запуске — окно обновления не предлагать.</summary>
    public static bool Holding { get; private set; }

    /// <summary>Ход установки словами; <c>null</c> — ничего не ставится.</summary>
    public static string? Status { get; private set; }

    /// <summary>Сменился <see cref="Status"/>. Зовётся из потока приложения.</summary>
    public static event Action? Changed;

    private static bool _ran;

    /// <summary>
    /// Ищет обновление и ставит его, если так выбрано. Звать из потока приложения,
    /// раз за процесс: при автозапуске — когда движки уже подняты (закачка
    /// пойдёт с обходом), при обычном запуске — сразу.
    /// </summary>
    /// <param name="tray">Программа запущена в трей автозапуском — туда и вернуться после подмены.</param>
    public static async Task RunAsync(bool tray)
    {
        if (_ran)
            return;

        _ran = true;
        Holding = true;

        var clock = Stopwatch.StartNew();
        ReleaseInfo? release = null;

        try
        {
            await UpdateNotice.CheckAsync(AppSettings.Load(AppSettings.DefaultPath), CancellationToken.None);

            release = UpdateNotice.Available;

            if (!UpdateNotice.ShouldInstallAtStart(release, AppSettings.Load(AppSettings.DefaultPath)))
                return;

            Journal.Write("обновление", $"при запуске найдена {release!.Version} — ставлю сама: выбрано автообновление");

            bool started = await UpdateRun.InstallAsync(
                release,
                new Progress<string>(Show),
                toTray: tray,
                stillWanted: () =>
                {
                    if (clock.Elapsed > InstallWithin)
                    {
                        Journal.Write("обновление",
                            $"при запуске: {release.Version} скачивалась {clock.Elapsed.TotalMinutes:0} мин — "
                            + "не ставлю без спроса, предложу окном");
                        return false;
                    }

                    // Пока качалось, человек мог выключить автообновление или пропустить версию.
                    return UpdateNotice.ShouldInstallAtStart(release, AppSettings.Load(AppSettings.DefaultPath));
                });

            if (!started)
                Show(null);
        }
        catch (Exception ex)
        {
            Journal.Write("обновление", "при запуске не поставилась: " + ex.GetBaseException().Message);
            Show(null);
        }
        finally
        {
            Holding = false;

            // Не поставилась — пусть окно предложит её как обычно: находку
            // объявляем заново, окно ловит это тем же событием.
            if (!App.Exiting)
                UpdateNotice.Remember(release);
        }
    }

    private static void Show(string? text)
    {
        Status = text;
        Changed?.Invoke();
    }
}
