using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Строка одного рецепта в окне выбора.</summary>
public sealed record RecipeRow
{
    /// <summary>Что запишется в правило: имя секции пресета.</summary>
    public required string Name { get; init; }

    /// <summary>Что показывается в заголовке: приёмы, а не сервис.</summary>
    public required string Title { get; init; }

    /// <summary>Набор целиком, с настройками.</summary>
    public required string Summary { get; init; }

    /// <summary>Где ещё этот набор применяется — чтобы судить по знакомому.</summary>
    public required string UsedBy { get; init; }

    public string Verdict { get; set; } = string.Empty;

    public Brush VerdictColour { get; set; } = Brushes.Transparent;

    public Brush Edge { get; set; } = Brushes.Transparent;
}

/// <summary>
/// Выбор рецепта десинка для одного имени.
/// </summary>
/// <remarks>
/// <para>
/// Заведено потому, что <c>mode: desync</c> в маршрутах означал ровно одно —
/// «мимо туннеля». Чем чинить имя, решал пресет, и если оно не попадало
/// ни в один его список, не делалось ничего: снаружи это неотличимо
/// от неработающего десинка. Хуже того, попасть можно в секцию с рецептом
/// <c>pass</c> — она забирает имя себе и пропускает нетронутым.
/// </para>
/// <para>
/// Рецепты берутся из самого пресета: там они подобраны под живые блокировки
/// и обновляются вместе с ним. Свой перечень мы бы выдумали один раз
/// и оставили стареть.
/// </para>
/// </remarks>
public partial class RecipeWindow : Window
{
    private readonly string _domain;
    private readonly ZapretPreset _preset;
    private readonly List<RecipeRow> _rows = [];

    private CancellationTokenSource? _work;

    /// <summary>Что выбрали; <c>null</c> — окно закрыли отменой.</summary>
    public string? Chosen { get; private set; }

    public RecipeWindow(string domain, ZapretPreset preset)
    {
        InitializeComponent();

        _domain = domain;
        _preset = preset;

        Head.Text = $"Чем чинить {domain}";

        foreach (var recipe in DesyncRecipes.FromPresetFile(preset))
        {
            _rows.Add(new RecipeRow
            {
                Name = recipe.Name,
                Title = recipe.Title,
                Summary = recipe.Detail,
                UsedBy = recipe.Where,
                Edge = (Brush)FindResource("Border"),
            });
        }

        Recipes.ItemsSource = _rows;

        Status.Text = _rows.Count == 0
            ? "В пресете нет ни одного рецепта — выбирать не из чего."
            : $"Рецептов в пресете «{preset.Name}»: {_rows.Count}. "
              + "Проверка подбирает рабочий сама, но требует остановленных движков.";

        TestButton.IsEnabled = _rows.Count > 0;
        Closed += (_, _) => _work?.Cancel();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;

        Chosen = name;
        DialogResult = true;
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        Chosen = DesyncRecipes.FromPreset;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Перебирает рецепты и смотрит, с каким имя открывается.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Движки обязаны быть остановлены, и это не перестраховка. Работающий
    /// winws2 уже держит свой набор фильтров, а второй экземпляр поверх него
    /// правил бы те же пакеты дважды — вышел бы замер не рецепта, а их суммы.
    /// Туннель тем более: через него имя откроется любым рецептом, включая
    /// пустой, и проверка ответит «работает всё».
    /// </para>
    /// <para>
    /// Останавливать их сами не беремся: это весь трафик машины, и человек
    /// должен решить это сам, а не обнаружить постфактум.
    /// </para>
    /// </remarks>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
        {
            Status.Text = "Сначала остановите движки на «Главной». Работающий десинк правил бы "
                + "те же пакеты вторым слоем, а туннель открыл бы имя любым рецептом — "
                + "проверять было бы нечего.";

            return;
        }

        var paths = ZapretPaths.Discover();
        var winws = paths is null ? null : Path.Combine(paths.Root, "exe", "winws2.exe");

        if (winws is null || !File.Exists(winws))
        {
            Status.Text = "winws2 не найден рядом с программой — проверять нечем.";
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        TestButton.IsEnabled = false;
        TestButton.Content = "Проверяю…";

        try
        {
            // Сперва без всякого рецепта: если имя открывается само, дальше
            // мерить нечего и выбирать не из чего.
            var plain = await BlockCheck.ProbeOnceAsync(_domain, null, _work.Token);

            if (plain.Kind == BlockKind.None)
            {
                Status.Text = $"{_domain} открывается и без десинка. Рецепт ему не нужен — "
                    + "берите «решает пресет».";

                return;
            }

            int worked = 0;
            int tried = 0;

            foreach (var row in _rows)
            {
                Status.Text = $"Проверяю {++tried} из {_rows.Count}: {row.Title}…";

                row.Verdict = "проверяю…";
                row.VerdictColour = (Brush)FindResource("Muted");
                Redraw();

                bool ok = await TryAsync(winws, paths!.Root, row.Name, _work.Token);

                row.Verdict = ok ? "открывается" : "не помогает";
                row.VerdictColour = (Brush)FindResource(ok ? "Accent" : "Danger");
                row.Edge = (Brush)FindResource(ok ? "Accent" : "Border");

                if (ok)
                    worked++;

                Redraw();
            }

            Status.Text = worked == 0
                ? $"Ни один рецепт не открыл {_domain}. Дело может быть не в десинке — "
                  + "посмотрите «Проверку блокировок» целиком."
                : $"Открывают {worked} из {_rows.Count}. Берите любой из отмеченных — "
                  + "если сомневаетесь, тот, что применяется к знакомому сервису.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Проверка прервана.";
        }
        catch (Exception ex)
        {
            Status.Text = "Проверка не удалась: " + ex.GetBaseException().Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestButton.Content = "Проверить все";
        }
    }

    /// <summary>
    /// Поднимает winws2 с одним профилем и пробует открыть имя.
    /// </summary>
    /// <remarks>
    /// Профиль ровно один, без остального пресета: иначе имя могла бы забрать
    /// чужая секция, и замер сказал бы о ней, а не о выбранном рецепте.
    /// </remarks>
    private async Task<bool> TryAsync(
        string winws,
        string zapretRoot,
        string recipeName,
        CancellationToken cancellationToken)
    {
        var recipe = DesyncRecipes.Find(_preset, recipeName);

        if (recipe is null || recipe.Steps.Count == 0)
            return false;

        var list = Path.GetFullPath(Path.Combine(
            WinwsCommandLine.OwnListsDirectory, "probe.txt"));

        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        await File.WriteAllTextAsync(list, _domain + Environment.NewLine, cancellationToken);

        var start = new ProcessStartInfo(winws)
        {
            WorkingDirectory = zapretRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Глобальные ключи пресета обязательны, и это выяснилось дорого:
        // без них проверка отвечала «не помогает» на каждый рецепт подряд,
        // включая заведомо рабочие. В них три вещи, без которых рецепт —
        // пустой звук:
        //
        //   --lua-init   загружает библиотеку, где эти самые fake,
        //                multidisorder и hostfakesplit_multi определены;
        //                без неё имя в --lua-desync не значит ничего;
        //   --wf-*       фильтр WinDivert, то есть какой трафик вообще
        //                перехватывать; без него не перехватывается ничего;
        //   --blob       заготовки поддельных пакетов, на которые рецепты
        //                ссылаются по имени (blob=tls_google).
        foreach (var argument in _preset.GlobalArguments)
            start.ArgumentList.Add(argument);

        start.ArgumentList.Add("--new");
        start.ArgumentList.Add("--name=NetZapret: проба");
        start.ArgumentList.Add("--filter-tcp=80,443");
        start.ArgumentList.Add($"--hostlist={list}");

        foreach (var step in recipe.Steps)
            start.ArgumentList.Add($"--lua-desync={step}");

        Process? process = null;

        try
        {
            process = Process.Start(start);

            if (process is null)
                return false;

            // Ждём, пока драйвер встанет в разрыв, — но по его же словам,
            // а не по часам. Раньше здесь стояли глухие две секунды на каждый
            // рецепт: на десятке это двадцать секунд чистого ожидания, и почти
            // всё оно лишнее — winws2 обычно готов за треть секунды.
            await ReadyAsync(process, cancellationToken);

            return await OpensAsync(_domain, cancellationToken);
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4000);
                }
            }
            catch (Exception)
            {
                // Процесс мог уйти сам — например, отказавшись от кривого
                // рецепта. Это и есть его отрицательный результат.
            }

            process?.Dispose();
        }
    }

    /// <summary>
    /// Ждёт, пока winws2 доложит о готовности.
    /// </summary>
    /// <remarks>
    /// Он печатает загрузку списков и число профилей, и последняя такая
    /// строка означает, что фильтр поставлен. Ждать по часам вслепую значило
    /// бы либо торопиться — и мерить незащищённое соединение, — либо
    /// закладывать запас на каждый рецепт подряд.
    /// </remarks>
    private static async Task ReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        var line = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        while (DateTime.UtcNow < deadline)
        {
            var finished = await Task.WhenAny(line, Task.Delay(300, cancellationToken));

            if (finished != line)
                continue;

            if (line.Result is null)
                break;

            // «we have N user defined desync profile(s)» — последняя строка
            // разбора настроек, дальше он уже слушает сеть.
            if (line.Result.Contains("desync profile", StringComparison.OrdinalIgnoreCase))
                break;

            line = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        }

        // Небольшая доводка всё же нужна: между разбором настроек и первым
        // перехваченным пакетом драйвер успевает не всегда.
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
    }

    /// <summary>
    /// Открывается ли имя: одно рукопожатие TLS, и всё.
    /// </summary>
    /// <remarks>
    /// Полная проба спрашивает TCP, TLS 1.2, TLS 1.3, HTTP и передачу данных —
    /// пять ответов там, где нужен один. Рецепт чинит ровно приветствие TLS,
    /// и вопрос к нему ровно один: дошло ли оно. На десятке рецептов разница
    /// между «одно рукопожатие» и «пять проб» — это минуты.
    /// </remarks>
    private static async Task<bool> OpensAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));

            await client.ConnectAsync(host, 443, deadline.Token);

            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
            }, deadline.Token);

            return true;
        }
        catch (Exception)
        {
            // Молчание, сброс, отказ — для нас это одно: не открылось.
            return false;
        }
    }

    private void Redraw()
    {
        Recipes.ItemsSource = null;
        Recipes.ItemsSource = _rows;
    }
}
