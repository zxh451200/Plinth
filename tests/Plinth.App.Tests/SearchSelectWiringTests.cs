using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views.Controls;
using Xunit;

namespace Plinth.App.Tests;

public sealed class SearchSelectWiringTests
{
    [AvaloniaFact]
    public void Typing_filters_the_bound_list()
    {
        var select = new SearchSelect
        {
            Items = new[]
            {
                new SettingOption("zh-CN", "中文（简体，中国）"),
                new SettingOption("zh-HK", "中文（繁體字，香港特別行政區）"),
                new SettingOption("en-US", "English (United States)"),
            },
        };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        window.Activate();
        window.Measure(new Size(420, 320));
        window.Arrange(new Rect(0, 0, 420, 320));

        var search = select.SearchBox;
        var list = select.OptionsList;

        search.Text = "香港";

        Assert.Equal("香港", search.Text);
        var hit = Assert.Single(list.Items.Cast<ISelectOption>());
        Assert.Equal("zh-HK", hit.Key);
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_a_searchable_flyout_focuses_the_search_box()
    {
        var select = new SearchSelect
        {
            Items = new[] { new SettingOption("zh-CN", "简体中文") },
        };
        var window = new Window { Content = select, Width = 420, Height = 320 };
        window.Show();
        var button = select.GetVisualDescendants().OfType<Button>().First();
        var point = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // 打开即可直接打字过滤,不用再点一次搜索框。
        Assert.True(select.SearchBox.IsFocused);
        window.Close();
    }
}
