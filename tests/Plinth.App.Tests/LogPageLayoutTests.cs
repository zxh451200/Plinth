using System;
using Avalonia;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>日志页布局稳定性:详情区高度固定,选中记录不能把列表压塌。</summary>
[Collection("Localization")]
public sealed class LogPageLayoutTests
{
    [AvaloniaFact]
    public void Loading_state_does_not_move_or_resize_the_table()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var nav = shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.LogsId);
        shell.ViewModel.OpenTabCommand.Execute(nav);
        shell.Layout();

        var page = shell.PageView<LogView>();
        var table = page.GetVisualDescendants().OfType<TableView>().Single();
        var logs = (LogViewModel)shell.ViewModel.SelectedTab!.Content;
        var before = table.TranslatePoint(new Avalonia.Point(0, 0), shell.Window);
        var beforeHeight = table.Bounds.Height;

        logs.IsLoading = true;
        shell.Layout();
        Assert.Equal(before, table.TranslatePoint(new Avalonia.Point(0, 0), shell.Window));
        Assert.Equal(beforeHeight, table.Bounds.Height);

        logs.IsLoading = false;
        shell.Layout();
        Assert.Equal(before, table.TranslatePoint(new Avalonia.Point(0, 0), shell.Window));
        Assert.Equal(beforeHeight, table.Bounds.Height);
    }

    [AvaloniaFact]
    public void Selecting_an_entry_keeps_the_table_height()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var nav = shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.LogsId);
        shell.ViewModel.OpenTabCommand.Execute(nav);
        shell.Layout();

        var page = shell.PageView<LogView>();
        var table = page.GetVisualDescendants().OfType<TableView>().Single();
        var before = table.Bounds.Height;
        Assert.True(before >= 100, $"列表初始高度只有 {before},详情区占得太多");

        var logs = (LogViewModel)shell.ViewModel.SelectedTab!.Content;
        logs.Entries.Add(new LogEntry(DateTimeOffset.Now, "Information", "layout probe"));
        shell.Layout();
        logs.SelectedEntry = logs.Entries[^1];
        shell.Layout();

        Assert.Equal(before, table.Bounds.Height);
    }
}
