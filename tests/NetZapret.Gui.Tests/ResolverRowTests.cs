using System.Windows;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Строка резолвера на вкладке DNS извещает окно обо всех производных свойствах:
/// без этого смена выбора, задержки, значка или пометки оставалась бы невидимой
/// после удаления полного пересоздания списка (Redraw, 27.11).
/// </summary>
public sealed class ResolverRowTests
{
    [Fact]
    public void Changing_fields_notifies_bound_properties()
    {
        var row = new ResolverRow
        {
            Name = "Google",
            Address = "8.8.4.4",
            Note = "надёжный, без фильтрации",
        };

        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        row.Address = "8.8.8.8";
        Assert.Contains(nameof(ResolverRow.Address), raised);
        Assert.Contains(nameof(ResolverRow.Shown), raised);

        raised.Clear();
        row.Latency = "18 мс";
        row.Key = "Accent";
        Assert.Contains(nameof(ResolverRow.Latency), raised);
        Assert.Contains(nameof(ResolverRow.Color), raised);

        raised.Clear();
        row.Chosen = true;
        Assert.Contains(nameof(ResolverRow.Chosen), raised);
        Assert.Contains(nameof(ResolverRow.Edge), raised);
        Assert.Contains(nameof(ResolverRow.MarkShown), raised);
        Assert.Equal(Visibility.Visible, row.MarkShown);

        // Повторная запись того же значения не должна поднимать лишние уведомления.
        raised.Clear();
        row.Chosen = true;
        row.Latency = "18 мс";
        Assert.Empty(raised);

        raised.Clear();
        row.Badge = "не тронут";
        row.BadgeKey = "Accent";
        row.BadgeTip = "проверено";
        Assert.Contains(nameof(ResolverRow.Badge), raised);
        Assert.Contains(nameof(ResolverRow.BadgeShown), raised);
        Assert.Contains(nameof(ResolverRow.BadgeBrush), raised);
        Assert.Contains(nameof(ResolverRow.BadgeTip), raised);
        Assert.Equal(Visibility.Visible, row.BadgeShown);

        Assert.Equal(Visibility.Collapsed, row.ServiceIconsShown);
        Assert.Equal(["Надёжный", "Без фильтрации"], row.Tags);
        Assert.Same(row.Tags, row.Tags);
    }
}
