using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace NetZapret.Mobile.Shell;

/// <summary>
/// Раздел телефона: заголовок, прокручиваемое содержимое, жизнь по показу.
/// </summary>
/// <remarks>
/// <para>
/// Разделы нижнего меню живут всё время работы приложения: проверка
/// блокировок идёт минуты, и уход на соседнюю вкладку не должен её
/// обрывать. Опросы (состояние движка, задержка выхода) идут только пока
/// раздел на экране — <see cref="Shown"/>/<see cref="Hidden"/>: телефону
/// незачем будить процессор ради невидимого.
/// </para>
/// <para>
/// Всё долгое — в фоне (<see cref="Task.Run(Func{Task})"/>), в поток окна —
/// только раскладка готового. На Windows чтение «Маршрутов» в потоке окна
/// стоило 470–916 мс на каждую смену маршрута (CLAUDE.md, «Вид окна»).
/// </para>
/// </remarks>
internal abstract class Section : UserControl
{
    private CancellationTokenSource? _visible;

    protected Section(MainView shell)
    {
        Shell = shell;
        Body = new StackPanel { Spacing = 12, Margin = new Thickness(16, 12, 16, 24) };

        Content = new ScrollViewer
        {
            Content = Body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    public MainView Shell { get; }

    protected IMobileHost Host => Shell.Host;

    /// <summary>Заголовок в шапке раздела.</summary>
    public abstract string Title { get; }

    /// <summary>Содержимое раздела — карточки сверху вниз.</summary>
    protected StackPanel Body { get; }

    /// <summary>Отменяется, когда раздел уходит с экрана.</summary>
    protected CancellationToken WhileVisible => _visible?.Token ?? CancellationToken.None;

    public bool IsVisibleNow => _visible is not null;

    public void Show()
    {
        _visible?.Cancel();
        _visible = new CancellationTokenSource();
        Shown();
    }

    public void Hide()
    {
        _visible?.Cancel();
        _visible?.Dispose();
        _visible = null;
        Hidden();
    }

    protected virtual void Shown() { }

    protected virtual void Hidden() { }

    /// <summary>Действие для шапки справа (обновить, добавить); <c>null</c> — нет.</summary>
    public virtual Control? HeaderAction => null;

    /// <summary>
    /// Читает в фоне и отдаёт результат в поток окна; ошибка — тоже в поток окна.
    /// </summary>
    protected void Load<T>(Func<T> read, Action<T> show, Action<Exception>? failed = null)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var value = read();
                Dispatcher.UIThread.Post(() => show(value));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (failed is not null)
                        failed(ex);
                    else
                        Shell.Say("Не прочиталось: " + ex.GetBaseException().Message);
                });
            }
        });
    }

    /// <summary>Пустое место с одной фразой — пока данные читаются.</summary>
    protected static Control Placeholder(string text)
    {
        var block = Ui.Text(text, "muted");
        block.HorizontalAlignment = HorizontalAlignment.Center;
        block.Margin = new Thickness(0, 24);
        return block;
    }
}
