using System.Diagnostics;
using System.IO;
using NetZapret.Core;

namespace NetZapret.Gui;

/// <summary>Чем закончилось управление движками.</summary>
internal sealed record EngineOutcome(bool Ok, string Message);

/// <summary>
/// Запуск, остановка и перезапуск движков — одним путём для всего окна.
/// </summary>
/// <remarks>
/// Прежде это лежало в трёх местах: на «Главной», в «VPN» и в уведомлении
/// главного окна. Каждое звало консольную программу само, и когда сборка
/// конфига появилась перед запуском, добавить её пришлось бы во все три —
/// то есть однажды забыть в одном и получить движки со старым конфигом
/// ровно там, где настройку только что и меняли.
/// </remarks>
internal static class EngineControl
{
    public static async Task<EngineOutcome> StartAsync(CancellationToken cancellationToken)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var note = string.Empty;

        // Конфиг собирается при каждом запуске: иначе смена сервера, правки
        // маршрутов и переключение режима показываются новыми, а до туннеля
        // не доходят.
        if (settings.NeedsProxy)
        {
            var built = await TunnelConfig.BuildAsync(settings, cancellationToken);

            if (!built.Ok)
            {
                // Прежний конфиг — не повод не запуститься: он мог просто
                // устареть, а обход по старому лучше, чем никакого.
                if (!File.Exists(settings.ProxyConfigPath))
                    return new EngineOutcome(false, built.Message);

                note = built.Message + " Запускаю с ранее собранным конфигом. ";
            }
        }

        var exe = Environment.ProcessPath;

        if (exe is null)
            return new EngineOutcome(false, "Не удалось определить путь к программе.");

        try
        {
            // Та же программа с ключом супервизора, а не консольная рядом:
            // окно обязано работать и там, где её нет вовсе. Без повышения —
            // окно и так запущено от администратора, а движкам нужны
            // именно его права.
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = SupervisorHost.BuildArguments(settings),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            // Выбор сервера подтверждаем движку отдельно, когда он поднимется.
            _ = AssertServerAsync(settings.PreferredServer);

            return new EngineOutcome(true, note + "Движки поднимаются.");
        }
        catch (Exception ex)
        {
            return new EngineOutcome(false, "Не удалось запустить: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Говорит движку, какой сервер выбран, когда тот поднимется.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Селектор в конфиге получает <c>default</c> с закреплённым сервером,
    /// но движок его перебивает: он помнит прошлый выбор в своём кэше, а кэш
    /// переживает и перезапуск, и пересборку конфига. Отключить это нечем —
    /// поля <c>store_selected</c> сборка 1.14 не знает и отвергает конфиг
    /// целиком вместе с туннелем.
    /// </para>
    /// <para>
    /// Стоило это дорого. В настройках стояла Польша, а весь трафик шёл через
    /// Cloudflare WARP, залипший там с давних проб; WARP к тому времени лёг,
    /// и вместе с ним лёг туннель. Программа при этом уверенно показывала
    /// Польшу — она читает настройки, а не движок, — и найти расхождение
    /// удалось только спросив сам движок через его API.
    /// </para>
    /// <para>
    /// Поэтому выбор подтверждается вслух. Отдельной задачей и без ожидания:
    /// движку нужно секунд пять на подъём, и держать ради этого кнопку
    /// «Запустить» незачем. Не получилось — не беда: селектор останется
    /// на том, что помнит, и это ровно прежнее поведение.
    /// </para>
    /// </remarks>
    private static async Task AssertServerAsync(string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred))
            return;

        try
        {
            var api = new Proxy.ClashApi();

            // Движок поднимается не мгновенно; ждём его появления, а не часов.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));

                if (await api.AliveAsync(CancellationToken.None))
                    break;
            }

            await api.SelectAsync("auto", preferred!, CancellationToken.None);
        }
        catch (Exception)
        {
            // Движок мог не подняться вовсе — об этом скажет «Главная».
        }
    }

    public static Task<string> StopAsync(CancellationToken cancellationToken) =>
        SupervisorHost.StopAsync(cancellationToken);

    public static async Task<EngineOutcome> RestartAsync(CancellationToken cancellationToken)
    {
        await SupervisorHost.StopAsync(cancellationToken);

        // Пауза, а не гонка: супервизор освобождает TUN и снимает фильтр
        // не мгновенно, и запуск, начатый сразу, наткнулся бы на ещё живой
        // адаптер — sing-box падает с «Cannot create a file when that file
        // already exists» и не поднимается ни разу из трёх попыток.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        return await StartAsync(cancellationToken);
    }
}
