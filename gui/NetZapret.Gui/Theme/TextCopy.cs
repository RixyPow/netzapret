using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Правый щелчок по любому тексту окна — меню «Копировать».
/// </summary>
/// <remarks>
/// <para>
/// Просьба пользователя (04.10, через владельца): «нужно скопировать домен из
/// проверки, а копировать нельзя». Надписи WPF (<see cref="TextBlock"/>) не
/// выделяются, а переделывать сотни их в поля только ради выделения — ломать
/// вид каждой вкладки. Поэтому одно правило на всё окно: обработчик на класс
/// TextBlock, без правки разметки.
/// </para>
/// <para>
/// Берётся полный текст надписи, а не видимый: имя, обрезанное многоточием,
/// копируется целиком. В строке списка (элемент ItemsControl) есть и второй
/// пункт — вся строка: имя, вердикт, задержка — чтобы прислать её в обсуждение.
/// </para>
/// <para>
/// Не мешает своим меню: там, где у надписи или её родителя меню уже есть
/// (серверы на вкладке VPN), открывается оно. Значки шрифта значков, поля
/// ввода (у них своё меню) и меню трея (оно прячется, теряя фокус) — мимо.
/// </para>
/// </remarks>
internal static class TextCopy
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;

        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            UIElement.MouseRightButtonUpEvent,
            new MouseButtonEventHandler(OnRightClick));
    }

    private static void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not TextBlock block || MenuFor(block) is not { } menu)
            return;

        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Меню копирования для надписи; <c>null</c> — копировать нечего или меню у неё своё.</summary>
    internal static ContextMenu? MenuFor(TextBlock block)
    {
        if (Window.GetWindow(block) is TrayMenu || HasOwnMenu(block))
            return null;

        var text = TextOf(block);

        if (string.IsNullOrWhiteSpace(text) || IsGlyph(block, text))
            return null;

        var menu = new ContextMenu
        {
            PlacementTarget = block,
            Placement = PlacementMode.MousePoint,
        };

        menu.Items.Add(Item($"Копировать «{Short(text)}»", text));

        if (RowText(block) is { } row && row != text)
            menu.Items.Add(Item("Копировать строку целиком", row));

        return menu;
    }

    /// <summary>Своё меню у надписи или выше — или поле ввода со своим.</summary>
    private static bool HasOwnMenu(DependencyObject start)
    {
        for (DependencyObject? node = start; node is not null; node = Parent(node))
        {
            if (node is TextBoxBase)
                return true;

            if (node is FrameworkElement { ContextMenu: not null })
                return true;
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private static string TextOf(TextBlock block) =>
        !string.IsNullOrEmpty(block.Text)
            ? block.Text
            : new TextRange(block.ContentStart, block.ContentEnd).Text;

    /// <summary>Значок из шрифта значков: знаки частной области Юникода, копировать нечего.</summary>
    private static bool IsGlyph(TextBlock block, string text) =>
        block.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase)
        || block.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
        || text.All(c => c is >= '' and <= '' || char.IsWhiteSpace(c));

    private static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();

        return line.Length <= 40 ? line : line[..39] + "…";
    }

    /// <summary>
    /// Все надписи строки списка через « · »; не в списке — <c>null</c>.
    /// </summary>
    private static string? RowText(DependencyObject start)
    {
        for (DependencyObject? node = start; node is not null; node = Parent(node))
        {
            if (node is not FrameworkElement element || ItemsControl.ItemsControlFromItemContainer(element) is null)
                continue;

            var parts = new List<string>();
            Collect(element, parts);

            return parts.Count > 1 ? string.Join(" · ", parts) : null;
        }

        return null;
    }

    private static void Collect(DependencyObject node, List<string> parts)
    {
        // По Visibility, а не по IsVisible: тот ложен и у показанного, пока
        // окно не прошло отрисовку, — а спрятанную ветку пропускаем целиком.
        if (node is UIElement { Visibility: not Visibility.Visible })
            return;

        if (node is TextBlock block)
        {
            var text = TextOf(block).ReplaceLineEndings(" ").Trim();

            if (text.Length > 0 && !IsGlyph(block, text) && (parts.Count == 0 || parts[^1] != text))
                parts.Add(text);

            return;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Collect(VisualTreeHelper.GetChild(node, i), parts);
    }

    private static MenuItem Item(string header, string text)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Copy(text);
        return item;
    }

    /// <summary>
    /// В буфер обмена, с повтором: его держит другая программа — частое дело
    /// у менеджеров буфера, и тогда Windows отказывает на долю секунды.
    /// </summary>
    private static void Copy(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }
    }
}
