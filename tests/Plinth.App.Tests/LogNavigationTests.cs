using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

[Collection("Localization")]
public sealed class LogNavigationTests
{
    /// <summary>级别下拉按界面语言显示,筛选值仍是日志里的英文标记。</summary>
    [Fact]
    public void Level_filter_shows_localized_names_but_maps_to_english_values()
    {
        Loc.Initialize("zh-CN");
        using var logs = new LogViewModel(new FolderService(NullLogger<FolderService>.Instance));
        Assert.Equal(["全部级别", "详细", "调试", "信息", "警告", "错误", "致命", "严重"], logs.LevelOptions);
        logs.LevelIndex = 4;
        Assert.Equal("Warning", logs.Level);
        logs.Level = "Fatal";
        Assert.Equal(6, logs.LevelIndex);
    }

    [AvaloniaFact]
    public async Task Related_error_replaces_old_filters_and_keeps_information_context()
    {
        using var shell = new Shell();
        var id = Guid.NewGuid();
        var path = WriteLogs(id);
        LogViewModel? logs = null;
        try
        {
            await shell.ViewModel.ShowErrorsCommand.ExecuteAsync(null);
            logs = Assert.IsType<LogViewModel>(shell.ViewModel.SelectedTab!.Content);
            logs.Level = "Fatal";
            logs.Module = "missing";
            logs.TaskId = Guid.NewGuid().ToString();
            logs.ErrorCode = "missing";
            logs.Search = "no match";
            logs.From = DateTimeOffset.UtcNow.AddYears(1);
            logs.To = logs.From;
            await shell.ViewModel.ShowErrorLogsCommand.ExecuteAsync(
                new TaskError("TASK_FAILED", "Safe", "Inspect logs", false, id.ToString("N")));

            Assert.Same(logs, shell.ViewModel.SelectedTab.Content);
            Assert.Single(shell.ViewModel.Tabs.Where(tab => tab.Id == ShellNavigation.LogsId));
            Assert.Equal(id.ToString("D"), logs.CorrelationId);
            Assert.False(logs.OnlyProblems);
            Assert.Null(logs.From);
            Assert.Null(logs.To);
            Assert.All(new[] { logs.Level, logs.Module, logs.TaskId, logs.ErrorCode, logs.Search },
                value => Assert.Equal("", value));
            Assert.Equal(2, logs.Entries.Count);
            Assert.Contains(logs.Entries, entry => entry.Level == "Information");
            Assert.Contains(logs.Entries, entry => entry.Level == "Error");

            shell.Layout();
            shell.Press(Key.F, PhysicalKey.F, RawInputModifiers.Control);
            var search = shell.PageView<LogView>().FindControl<TextBox>("LogSearch")!;
            Assert.True(search.IsKeyboardFocusWithin);
        }
        finally
        {
            logs?.PageClosed();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Task_query_problem_preset_and_clear_reuse_the_same_log_page()
    {
        using var shell = new Shell();
        var id = Guid.NewGuid();
        var path = WriteLogs(id);
        LogViewModel? logs = null;
        try
        {
            var task = new WorkItem(id, "Export", "Demo", WorkStatus.Failed, null,
                DateTimeOffset.UtcNow, null, null, false, null);
            await shell.ViewModel.ShowTaskLogsCommand.ExecuteAsync(task);
            logs = Assert.IsType<LogViewModel>(shell.ViewModel.SelectedTab!.Content);
            Assert.Equal(id.ToString("D"), logs.TaskId);
            Assert.Equal(2, logs.Entries.Count);
            logs.SelectedEntry = logs.Entries[0];

            await shell.ViewModel.ShowErrorsCommand.ExecuteAsync(null);
            Assert.Same(logs, shell.ViewModel.SelectedTab.Content);
            Assert.True(logs.OnlyProblems);
            Assert.Equal(3, logs.EventKindIndex);
            Assert.Contains(logs.Entries, entry => entry.TaskId == id.ToString("D") && entry.Level == "Error");
            Assert.Equal("", logs.TaskId);
            Assert.Null(logs.SelectedEntry);
            Assert.DoesNotContain(logs.Entries, entry => entry.Level == "Information");

            await logs.ClearFiltersCommand.ExecuteAsync(null);
            Assert.False(logs.OnlyProblems);
            Assert.Equal("", logs.CorrelationId);
            Assert.Equal(0, logs.EventKindIndex);
            Assert.DoesNotContain(logs.Entries, entry => entry.TaskId == id.ToString("D"));
            await logs.OpenQueryAsync(new LogFilter(TaskId: id.ToString("D"), EventKind: LogEventKind.Task));
            Assert.Contains(logs.Entries, entry =>
                entry.TaskId == id.ToString("D") && entry.Level == "Information");
        }
        finally
        {
            logs?.PageClosed();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Superseded_query_cannot_replace_the_latest_results()
    {
        using var shell = new Shell();
        var id = Guid.NewGuid();
        var path = WriteLogs(id);
        LogViewModel? logs = null;
        try
        {
            await shell.ViewModel.ShowErrorsCommand.ExecuteAsync(null);
            logs = Assert.IsType<LogViewModel>(shell.ViewModel.SelectedTab!.Content);
            var oldQuery = logs.OpenQueryAsync(new LogFilter(Search: "no matching record"));
            var latestQuery = logs.OpenQueryAsync(new LogFilter(TaskId: id.ToString("D"), EventKind: LogEventKind.Task));
            await Task.WhenAll(oldQuery, latestQuery);
            Assert.False(logs.IsLoading);
            Assert.Equal(id.ToString("D"), logs.TaskId);
            Assert.Equal(2, logs.Entries.Count);
            Assert.All(logs.Entries, entry => Assert.Equal(id.ToString("D"), entry.TaskId));
        }
        finally
        {
            logs?.PageClosed();
            File.Delete(path);
        }
    }

    private static string WriteLogs(Guid id)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var path = Path.Combine(AppPaths.LogDirectory, $"navigation-{id:N}.ndjson");
        var lines = new[] { "Information", "Error" }.Select(level =>
            JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["@t"] = DateTimeOffset.UtcNow,
                ["@mt"] = "Operation context",
                ["@l"] = level,
                ["TaskId"] = id.ToString("D"),
                ["CorrelationId"] = id.ToString("D"),
            }));
        File.WriteAllLines(path, lines);
        return path;
    }
}
