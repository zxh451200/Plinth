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
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(420, 320));
        window.Arrange(new Rect(0, 0, 420, 320));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var card = select.FindControl<Border>("PopupCard")!;
        var rows = select.OptionsList.GetVisualDescendants().OfType<ListBoxItem>().ToList();

        Assert.Equal(0, card.Padding.Left);
        Assert.Equal(0, card.Padding.Right);
        Assert.Equal(3, rows.Count);
        // 首行离上边留 4px:选中底色不顶到卡片圆角,左右仍然通栏。
        var firstOrigin = rows[0].TranslatePoint(new Point(0, 0), card)!.Value;
        Assert.Equal(card.BorderThickness.Top + 4, firstOrigin.Y, 1);
        foreach (var row in rows)
        {
            // 行左缘贴住卡片内边(1px 细边),右缘对齐卡片右边:选/悬停底色通栏。
            var origin = row.TranslatePoint(new Point(0, 0), card)!.Value;
            Assert.Equal(card.BorderThickness.Left, origin.X, 1);
            Assert.Equal(card.Bounds.Width - card.BorderThickness.Right, origin.X + row.Bounds.Width, 1);
        }

        Assert.True(rows[1].IsSelected);

        // 浮层左缘、宽度与字段完全重合,并且留出 4px 间距,不再出现原生弹窗的像素错位。
        var fieldOrigin = button.TranslatePoint(new Point(0, 0), window)!.Value;
        var cardOrigin = card.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.Equal(fieldOrigin.X, cardOrigin.X, 1);
        Assert.Equal(button.Bounds.Width, card.Bounds.Width, 1);
        Assert.Equal(fieldOrigin.Y + button.Bounds.Height + 4, cardOrigin.Y, 1);
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

    [AvaloniaFact]
    public void Field_shows_chevron_and_open_state()
    {
        var select = new SearchSelect { Items = Options, SelectedItem = Options[0], Searchable = false };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var button = select.GetVisualDescendants().OfType<Button>().First();
        var chevron = button.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("chevron"));
        Assert.Equal("\uE70D", chevron.Text);
        Assert.False(button.Classes.Contains("open"));

        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(select.OptionsPopup.IsOpen);
        Assert.True(button.Classes.Contains("open"));

        select.OptionsPopup.IsOpen = false;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(button.Classes.Contains("open"));
        window.Close();
    }

    [AvaloniaFact]
    public void Arrow_keys_browse_without_committing_until_enter()
    {
        var select = new SearchSelect { Items = Options, SelectedItem = Options[0], Searchable = false };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        var button = select.GetVisualDescendants().OfType<Button>().First();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 上下键只移动高亮,设置值不变、弹层不关。
        Assert.Equal(1, select.OptionsList.SelectedIndex);
        Assert.Same(Options[0], select.SelectedItem);
        Assert.True(select.OptionsPopup.IsOpen);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(Options[1], select.SelectedItem);
        Assert.False(select.OptionsPopup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_an_option_commits_it()
    {
        var select = new SearchSelect { Items = Options, SelectedItem = Options[0], Searchable = false };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        var button = select.GetVisualDescendants().OfType<Button>().First();
        var fieldPoint = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(fieldPoint, MouseButton.Left);
        window.MouseUp(fieldPoint, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var row = select.OptionsList.GetVisualDescendants().OfType<ListBoxItem>().Last();
        var rowPoint = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(rowPoint, MouseButton.Left);
        window.MouseUp(rowPoint, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(Options[2], select.SelectedItem);
        Assert.False(select.OptionsPopup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Popup_surface_follows_the_dark_theme()
    {
        var previous = Application.Current!.RequestedThemeVariant;
        try
        {
            Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            var select = new SearchSelect { Items = Options, SelectedItem = Options[1], Searchable = false };
            var window = new Window { Content = select, Width = 420, Height = 320 };
            window.Show();
            select.OptionsPopup.IsOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var card = select.FindControl<Border>("PopupCard")!;
            var background = Assert.IsType<SolidColorBrush>(card.Background);
            // 弹层在独立根里,画刷必须按当前主题重新解析;深色下不能还是浅色面板。
            Assert.True(background.Color.R < 60 && background.Color.G < 60 && background.Color.B < 60,
                $"深色弹层底色过浅:{background.Color}");
            window.Close();
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previous;
        }
    }
}
