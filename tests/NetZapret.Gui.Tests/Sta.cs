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
    /// <c>Application</c> в процессе один и переживает поток, который его
    /// создал. Второй вызов конструктора кидает, поэтому проверяем — и под
    /// замком: тесты идут по очереди внутри класса, но классов несколько.
    /// </remarks>
    private static void Prepare()
    {
        lock (Gate)
        {
            if (Application.Current is not null)
                return;

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
    }
}
