using System.Diagnostics;
using System.IO;
using System.Windows;
using NetZapret.Core;
using NetZapret.Core.Updates;

namespace NetZapret.Gui;

/// <summary>
/// Установка обновления: закачка, остановка движков, подмена и закрытие.
/// </summary>
/// <remarks>
/// <para>
/// Одна на кнопку «Обновить» (<see cref="Views.UpdatePanel"/>) и установку
/// при запуске (<see cref="StartupUpdate"/>, 12.11): прежде жила в карточке
/// «Главной», а при запуске в трей «Главной» нет вовсе.
/// </para>
/// <para>
/// Движки останавливаются до подмены, а не после. С 0.5.0 супервизор — это та же
/// программа с ключом, и пока он работает, Windows держит её файл: подмена
/// сорвалась бы на самом главном файле. Но после закачки, а не до неё (04.10):
/// закачка идёт с обходом, а без обхода человек остаётся на секунды подмены.
/// Подменяет внешний процесс, потому что заменить нужно и себя.
/// </para>
/// </remarks>
internal static class UpdateRun
{
    /// <summary>Установка идёт — вторую не начинать и окно обновления не предлагать.</summary>
    public static bool Busy { get; private set; }

    /// <summary>
    /// Ставит <paramref name="release"/>; удалось — программа закрывается.
    /// </summary>
    /// <param name="say">Ход словами: «Скачиваю…», «Останавливаю движки…».</param>
    /// <param name="fraction">Доля скачанного — для кнопки с процентами.</param>
    /// <param name="toTray">После подмены поднять программу в трей с движками.</param>
    /// <param name="stillWanted">
    /// Спрашивается после закачки, перед остановкой движков: <c>false</c> —
    /// не ставить (скачанное остаётся до следующей чистки при запуске).
    /// </param>
    /// <returns><c>false</c> — не начато или отменено по <paramref name="stillWanted"/>.</returns>
    /// <exception cref="Exception">Закачка или подготовка подмены не удались — текст для человека.</exception>
    public static async Task<bool> InstallAsync(
        ReleaseInfo release,
        IProgress<string> say,
        IProgress<double>? fraction = null,
        bool toTray = false,
        Func<bool>? stillWanted = null)
    {
        if (Busy)
            return false;

        Busy = true;

        try
        {
            string via = string.Empty;

            var progress = new Progress<double>(share =>
            {
                say.Report($"Скачиваю {release.Version}{via}… {share * 100:0} %");
                fraction?.Report(share);
            });

            var note = new Progress<string>(_ => via = " через туннель");

            var plan = await UpdateInstaller.StageAsync(release, progress, CancellationToken.None, TunnelForDownload(), note);

            if (stillWanted is not null && !stillWanted())
                return false;

            say.Report("Останавливаю движки…");
            await EngineControl.StopAsync("обновление программы", CancellationToken.None);

            var installedAt = Path.GetFullPath(".");
            var script = UpdateInstaller.WriteApplyScript(plan, installedAt);

            // Что дописано руками в наши списки, переносится в новые — сказать
            // об этом сейчас: после подмены окна, которое могло бы сказать, нет.
            var carried = plan.CarriedLists.Count == 0
                ? string.Empty
                : " Ваши дописки в списки перенесены: "
                    + string.Join(", ", plan.CarriedLists.Select(c => $"{c.Key} — {c.Value}")) + ".";

            say.Report($"Скачано {plan.Files} файлов.{carried} Закрываюсь для подмены…");

            if (carried.Length > 0)
                Journal.Write("обновление", $"{release.Version}:{carried}");

            // Окно подмены — копией самой программы (UpdateApplier, 12.10);
            // не вышло (рабочая сборка, нет места) — прежний сценарий cmd.
            // Сценарий ключей не передаёт: в трей после него не вернуться,
            // программа откроется окном.
            if (!StartApplier(release, plan, installedAt, via, toTray))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = script,
                    // Пути — аргументами, а не текстом сценария: он в ASCII,
                    // и кириллица в пути становилась вопросами (12.10).
                    Arguments = UpdateInstaller.ApplyArguments(Environment.ProcessId, plan, installedAt),
                    UseShellExecute = true,
                });
            }

            App.Exiting = true;
            Application.Current.Shutdown();
            return true;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Вход в туннель для закачки — если туннель поднят и ему есть куда вести.
    /// </summary>
    /// <remarks>
    /// Вход проверки движка (health-in) ведёт в селектор выхода первым
    /// правилом. В «Десинке» движок туннеля поднят только ради DNS, и такой
    /// вход вёл бы напрямую — поэтому смотрим, нужен ли туннель настройкам.
    /// Пароль входа — из конфига работающего движка; нет его — без туннеля.
    /// </remarks>
    private static System.Net.IWebProxy? TunnelForDownload()
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (!settings.NeedsProxy || !settings.HasTunnelExit || !EngineControl.IsRunning)
                return null;

            return Proxy.EngineKeys.Current()?.Proxy(Proxy.SingBoxOptions.DefaultHealthPort);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Запускает окно подмены — копию программы с ключом подмены.
    /// </summary>
    /// <returns><c>false</c> — копией не обойтись, нужен сценарий cmd.</returns>
    /// <remarks>
    /// Без оболочки: копия наследует права администратора этого окна — ей
    /// гасить драйвер и писать в Program Files. Пути — списком аргументов,
    /// кириллица доходит как есть.
    /// </remarks>
    private static bool StartApplier(ReleaseInfo release, UpdatePlan plan, string installedAt, string via, bool toTray)
    {
        try
        {
            var copy = UpdateInstaller.PrepareApplier(Environment.ProcessPath, installedAt);

            if (copy is null)
                return false;

            string? notes = null;

            if (!string.IsNullOrWhiteSpace(release.Notes))
            {
                notes = Path.Combine(installedAt, UpdateInstaller.StagingDirectory, "notes.md");
                File.WriteAllText(notes, release.Notes);
            }

            var size = release.ArchiveSize > 0 ? $"{release.ArchiveSize / 1048576.0:0} МБ" : "Архив";
            var downloaded = size + (via.Length > 0 ? via : " напрямую");

            var start = new ProcessStartInfo(copy)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(copy)!,
            };

            foreach (var argument in UpdateInstaller.ApplierArguments(
                Environment.ProcessId, plan, installedAt, UpdateCheck.Current, release.Version, downloaded, notes, toTray))
            {
                start.ArgumentList.Add(argument);
            }

            using var started = Process.Start(start);
            return started is not null;
        }
        catch (Exception ex)
        {
            Journal.Write("обновление", "окно подмены не запустилось, иду сценарием: " + ex.GetBaseException().Message);
            return false;
        }
    }
}
