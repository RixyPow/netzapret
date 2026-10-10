using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Значок и стрелка меню в шаблоне кнопки (ButtonGlyph) и столбец,
/// показывающий только влезающие строки (FitStack), — «Наблюдение» 10.10.
/// </summary>
public sealed class ButtonGlyphTests
{
    [Theory]
    [InlineData("Ghost")]
    [InlineData("Primary")]
    public void TheIconStandsBeforeTheCaptionInTheButtonsColour(string style) => Sta.Run(() =>
    {
        var button = Make(style, "Очистить");
        ButtonGlyph.SetLead(button, "");
        Lay(button);

        var lead = (TextBlock)button.Template.FindName("LeadGlyph", button);

        Assert.Equal(Visibility.Visible, lead.Visibility);
        Assert.Equal("", lead.Text);
        Assert.Equal(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)lead.Foreground).Color);
    });

    [Fact]
    public void WithoutAnIconAButtonLooksAsBefore() => Sta.Run(() =>
    {
        var button = Make("Ghost", "Скопировать ссылку");
        Lay(button);

        Assert.Equal(Visibility.Collapsed, ((TextBlock)button.Template.FindName("LeadGlyph", button)).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)button.Template.FindName("MenuArrow", button)).Visibility);
    });

    [Fact]
    public void AMenuButtonShowsTheArrow() => Sta.Run(() =>
    {
        var button = Make("Ghost", "Программа: все");
        ButtonGlyph.SetMenu(button, true);
        Lay(button);

        Assert.Equal(Visibility.Visible, ((FrameworkElement)button.Template.FindName("MenuArrow", button)).Visibility);
    });

    [Fact]
    public void FitStackDrawsOnlyWholeLines() => Sta.Run(() =>
    {
        var stack = new FitStack();

        for (int i = 0; i < 5; i++)
            stack.Children.Add(new Border { Height = 20 });

        stack.Measure(new Size(100, 70));
        Assert.Equal(60, stack.DesiredSize.Height);

        stack.Arrange(new Rect(0, 0, 100, 70));

        // Три влезли по порядку, четвёртая и пятая — за нижним краем.
        Assert.Equal([0d, 20d, 40d, 70d, 70d], stack.Children.Cast<UIElement>().Select(c => VisualTreeHelper.GetOffset(c).Y));
        Assert.True(stack.ClipToBounds);
    });

    [Fact]
    public void FitStackWithoutALimitDrawsEverything() => Sta.Run(() =>
    {
        var stack = new FitStack();

        for (int i = 0; i < 5; i++)
            stack.Children.Add(new Border { Height = 20 });

        stack.Measure(new Size(100, double.PositiveInfinity));

        Assert.Equal(100, stack.DesiredSize.Height);
    });

    private static Button Make(string style, string caption) => new()
    {
        Style = (Style)Application.Current.FindResource(style),
        Content = caption,
    };

    private static void Lay(Button button)
    {
        button.Measure(new Size(400, 100));
        button.Arrange(new Rect(0, 0, 400, 100));
    }
}
