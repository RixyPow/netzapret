using System.Windows;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Запускает проверку в потоке, где WPF согласен работать.
/// </summary>
/// <remarks>
/// <para>
/// Нужен потому, что xUnit крутит тесты в пуле, а пуль многопоточен: всякий
/// <c>DispatcherObject</c> — а это каждый элемент окна — требует потока
/// с однопоточной моделью, и без неё падает на конструкторе.
/// </para>
/// <para>
/// Заодно здесь поднимается <see cref="Application"/> со словарями тем.
/// Без него <c>{StaticResource Card}</c> не находит ничего и разбор разметки
/// валится — то есть без этой обёртки проверка вкладок проверяла бы только
/// наличие обёртки.
/// </para>
/// </remarks>
internal static class Sta
{
    private static readonly object Gate = new();

    /// <summary>
    /// Выполняет <paramref name="action"/> в потоке STA и возвращает ошибку,
    /// если она случилась.
    /// </summary>
    /// <remarks>
    /// Исключение перебрасывается в поток теста, а не глотается: тест,
    /// молча проходящий при упавшем окне, хуже отсутствующего.
    /// </remarks>
    public static void Run(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                Prepare();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Диспетчер потока гасится, пока поток жив. Иначе окна теста,
                // подсказки и скрытое окно отрисовки переживают поток: Windows
                // разрушает их уже на выходе из него, их оконная процедура
                // зовёт управляемый код на разобранном потоке, и процесс тестов
                // падает целиком (FailFast в HwndSubclass.SubclassWndProc).
                // Так CI упал дважды подряд 05.10 на 192-м тесте, а соседний
                // коммит с тем же кодом прошёл — падение плавающее.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Без срока: висящий тест хуже упавшего только тем, что не назван.
        // Минута с запасом — разбор всех словарей тем занимает доли секунды.
        if (!thread.Join(TimeSpan.FromMinutes(1)))
            throw new TimeoutException("поток STA не завершился за минуту");

        if (failure is not null)
            throw new InvalidOperationException(failure.Message, failure);
    }

    /// <summary>
    /// Поднимает приложение со словарями тем, если его ещё нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Application</c> в процессе один, второй вызов конструктора кидает,
    /// поэтому проверяем — и под замком: тесты идут по очереди внутри класса,
    /// но классов несколько.
    /// </para>
    /// <para>
    /// Живёт на своём фоновом потоке, а не на потоке первого теста. WPF
    /// считает приложение завершающимся, как только погашен диспетчер его
    /// потока (Application.IsShuttingDown смотрит на HasShutdownStarted),
    /// и тогда Window.Show() молча ничего не делает. Прежде приложение
    /// создавал первый тест, а его поток Run гасит на выходе (см. выше) —
    /// и окна всех следующих тестов не показывались. Тестам, которым хватает
    /// разметки, это не мешало; HomeFitTests раскладывает показанное окно
    /// и в CI падал на пустом разделе всякий раз, когда шёл не первым
    /// (20.10: четыре сборки из восьми; в чистой копии — два-три полных
    /// прогона окна из пяти, в момент падения IsShuttingDown = true, окно
    /// невидимо и 0×0). На этом потоке окон нет, его диспетчер не гасится,
    /// а фоновый поток не держит процесс тестов на выходе.
    /// </para>
    /// </remarks>
    private static void Prepare()
    {
        lock (Gate)
        {
            if (Application.Current is not null)
                return;

            Exception? failure = null;
            using var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                try
                {
                    var app = new Application();

                    foreach (var theme in new[] { "Palette", "Controls" })
                    {
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri(
                                $"pack://application:,,,/NetZapret;component/Theme/{theme}.xaml",
                                UriKind.Absolute),
                        });
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    ready.Set();
                }

                if (failure is null)
                    System.Windows.Threading.Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WPF Application для тестов",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromMinutes(1)))
                throw new TimeoutException("приложение WPF не поднялось за минуту");

            if (failure is not null)
                throw new InvalidOperationException("приложение WPF не поднялось: " + failure.Message, failure);
        }
    }
}
