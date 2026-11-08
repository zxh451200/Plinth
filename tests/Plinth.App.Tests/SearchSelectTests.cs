using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Plinth.App.ViewModels;
using Plinth.App.Views.Controls;
using Xunit;

namespace Plinth.App.Tests;

public sealed class SearchSelectTests
{
    private static readonly SettingOption[] Options =
    [
        new("zh-CN", "中文（简体，中国）"),
        new("zh-HK", "中文（繁體字，香港特別行政區）"),
        new("en-US", "English (United States)"),
    ];

    [Fact]
    public void Empty_query_returns_every_option()
    {
        Assert.Equal(3, SearchSelect.Filter(Options, "").Count);
        Assert.Equal(3, SearchSelect.Filter(Options, "   ").Count);
        Assert.Equal(3, SearchSelect.Filter(Options, null).Count);
    }

    [Fact]
    public void Query_matches_title_ignoring_case_and_whitespace()
    {
        var hits = SearchSelect.Filter(Options, " 香港 ");
        var hit = Assert.Single(hits);
        Assert.Equal("zh-HK", hit.Key);

        Assert.Single(SearchSelect.Filter(Options, "english"));
        Assert.Empty(SearchSelect.Filter(Options, "不存在"));
    }

    [AvaloniaFact]
    public void Option_rows_span_the_full_popup_width()
    {
        var select = new SearchSelect { Items = Options, SelectedItem = Options[1], Searchable = false };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        var button = select.GetVisualDescendants().OfType<Button>().First();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.Flyout!.ShowAt(button);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(420, 320));
        window.Arrange(new Rect(0, 0, 420, 320));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var card = select.FindControl<Border>("PopupCard")!;
        var rows = select.OptionsList.GetVisualDescendants().OfType<ListBoxItem>().ToList();

        Assert.Equal(0, card.Padding.Left);
        Assert.Equal(0, card.Padding.Right);
        Assert.Equal(3, rows.Count);
        // 首行贴上边:弹层不含上下留白(没有搜索框时)。
        var firstOrigin = rows[0].TranslatePoint(new Point(0, 0), card)!.Value;
        Assert.Equal(card.BorderThickness.Top, firstOrigin.Y, 1);
        foreach (var row in rows)
        {
            // 行左缘贴住卡片内边(1px 细边),右缘对齐卡片右边:选/悬停底色通栏。
            var origin = row.TranslatePoint(new Point(0, 0), card)!.Value;
            Assert.Equal(card.BorderThickness.Left, origin.X, 1);
            Assert.Equal(card.Bounds.Width - card.BorderThickness.Right, origin.X + row.Bounds.Width, 1);
        }

        Assert.True(rows[1].IsSelected);
        window.Close();
    }

    [AvaloniaFact]
    public void Pressed_field_uses_the_app_hover_brush()
    {
        var select = new SearchSelect { Items = Options, SelectedItem = Options[0], Searchable = false };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(420, 320));
        window.Arrange(new Rect(0, 0, 420, 320));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var button = select.GetVisualDescendants().OfType<Button>().First();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
            .First(candidate => candidate.Name == "PART_ContentPresenter");
        Assert.NotNull(presenter.Background);
        var pressed = ((ISolidColorBrush)presenter.Background!).Color;

        // 按下态不能落回 Fluent 的 40% 黑底(#66000000,白底上呈 #999);应为应用浅灰。
        Assert.True(pressed.R > 200 && pressed.G > 200 && pressed.B > 200, $"按下底色过深:{pressed}");

        window.MouseUp(point, MouseButton.Left);
        window.Close();
    }
}
