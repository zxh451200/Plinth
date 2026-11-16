using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>示例数据页工具栏布局:搜索占标题行,显示列与批量选择同排同高,展开列开关不另起一行。</summary>
public sealed class DemoListToolbarLayoutTests
{
    [AvaloniaFact]
    public async Task Column_toggle_sits_on_the_batch_row_with_button_height()
    {
        using var page = await Page.CreateAsync();
        var batch = page.View.FindControl<Button>("ToggleBatchButton")!;
        var column = page.View.FindControl<ToggleButton>("ColumnToggle")!;

        var batchOrigin = batch.TranslatePoint(new Point(0, 0), page.View)!.Value;
        var columnOrigin = column.TranslatePoint(new Point(0, 0), page.View)!.Value;

        Assert.Equal(batchOrigin.Y, columnOrigin.Y, 1);
        Assert.True(columnOrigin.X > batchOrigin.X, "显示列应排在批量选择右侧");
        Assert.Equal(batch.Bounds.Height, column.Bounds.Height, 1);
        Assert.True(column.Bounds.Height < 40, $"显示列高 {column.Bounds.Height},不应再是整块展开器");
    }

    [AvaloniaFact]
    public async Task Column_menu_is_collapsed_until_the_toggle_is_checked()
    {
        using var page = await Page.CreateAsync();
        var menu = page.View.FindControl<StackPanel>("ColumnMenu")!;
        var toggle = page.View.FindControl<ToggleButton>("ColumnToggle")!;

        Assert.False(menu.IsVisible);
        Assert.NotEmpty(menu.Children);

        toggle.IsChecked = true;
        Assert.True(menu.IsVisible);

        toggle.IsChecked = false;
        Assert.False(menu.IsVisible);
    }

    [AvaloniaFact]
    public async Task Search_box_shares_the_title_row_and_keeps_to_the_right_edge()
    {
        using var page = await Page.CreateAsync();
        var search = page.View.FindControl<TextBox>("SearchBox")!;
        var title = page.View.GetVisualDescendants().OfType<SelectableTextBlock>()
            .First(block => block.FontSize >= 19);
        var action = page.View.FindControl<Button>("ToggleBatchButton")!;

        var searchOrigin = search.TranslatePoint(new Point(0, 0), page.View)!.Value;
        var titleOrigin = title.TranslatePoint(new Point(0, 0), page.View)!.Value;
        var actionOrigin = action.TranslatePoint(new Point(0, 0), page.View)!.Value;

        // 同一行:垂直中心对齐;且整组按钮在搜索框下方。
        Assert.Equal(titleOrigin.Y + title.Bounds.Height / 2, searchOrigin.Y + search.Bounds.Height / 2, 1);
        Assert.True(actionOrigin.Y > searchOrigin.Y + search.Bounds.Height);

        // 贴内容右缘(与表格右缘同一列宽),不再挤在按钮组末尾。
        var table = page.View.FindControl<TableView>("RecordsTable")!;
        var tableRight = table.TranslatePoint(new Point(table.Bounds.Width, 0), page.View)!.Value.X;
        Assert.Equal(tableRight, searchOrigin.X + search.Bounds.Width, 1);
    }

    private sealed class Page : IDisposable
    {
        private readonly TestDatabase _database = new();
        public DemoListViewModel Vm { get; }
        public DemoListView View { get; }
        public Window Window { get; }

        private Page()
        {
            Vm = new(new DemoService(_database.Factory, NullLogger<DemoService>.Instance),
                NullLogger<DemoListViewModel>.Instance, new UnsavedChangesService());
            View = new DemoListView { DataContext = Vm };
            Window = new Window { Content = View, Width = 1200, Height = 720 };
        }

        public static async Task<Page> CreateAsync()
        {
            var page = new Page();
            try
            {
                var service = new DemoService(page._database.Factory, NullLogger<DemoService>.Instance);
                await service.SaveAsync(new DemoRecord { Code = "C000", Name = "Record 0" });
                page.Vm.PageSize = 25;
                await page.Vm.LoadAsync();
                page.Window.Show();
                Dispatcher.UIThread.RunJobs();
                page.Window.Measure(new Size(1200, 720));
                page.Window.Arrange(new Rect(0, 0, 1200, 720));
                Dispatcher.UIThread.RunJobs();
                return page;
            }
            catch { page.Dispose(); throw; }
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            _database.Dispose();
        }
    }
}
