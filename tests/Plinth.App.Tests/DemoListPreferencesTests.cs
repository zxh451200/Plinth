using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoListPreferencesTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-list-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "workspace.json");
    private DemoService Service => new(_database.Factory, NullLogger<DemoService>.Instance);
    private DemoListViewModel Create(WorkspacePreferencesService store, UnsavedChangesService? changes = null) =>
        new(Service, NullLogger<DemoListViewModel>.Instance, changes, store);

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("中文筛选", 4)]
    public void Search_defaults_and_null_are_normalized(string? search, int length)
    {
        var preferences = JsonSerializer.Deserialize<DemoListPreferences>(
            JsonSerializer.Serialize(new { SearchText = search }))!;
        Assert.Equal(length, preferences.SearchText.Length);
        Assert.Empty(JsonSerializer.Deserialize<DemoListPreferences>("{}")!.SearchText);
        Assert.Equal(500, new DemoListPreferences { SearchText = new string('中', 501) }.SearchText.Length);
    }

    [Fact]
    public async Task Search_is_bounded_persisted_reopened_and_kept_in_backup()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        using (var vm = Create(store))
        {
            vm.SearchText = new string('中', 501);
            await vm.LoadAsync();
            Assert.Equal(500, vm.SearchText.Length);
        }
        using var reopened = Create(new WorkspacePreferencesService(ConfigPath));
        await reopened.LoadAsync();
        Assert.Equal(new string('中', 500), reopened.SearchText);
        var normalized = WorkspacePreferencesService.NormalizeForBackup(
            """{"Demo":{"SearchText":"中文筛选","Unknown":"discard"}}""");
        var backup = JsonSerializer.Deserialize<WorkspacePreferencesService.Preferences>(normalized)!;
        Assert.Equal("中文筛选", backup.Demo!.SearchText);
        Assert.DoesNotContain("Unknown", normalized);
    }

    private WorkspacePreferencesService SavedStore()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.True(store.SaveDemo(new DemoListPreferences
        {
            SearchText = "中文",
            PageSize = 100,
            SortField = "Name",
            Descending = true,
            Columns = new() { ["Remark"] = new(false, 350) },
        }));
        return store;
    }

    private static void AssertDefaults(DemoListViewModel vm)
    {
        Assert.Empty(vm.SearchText);
        Assert.Equal(1, vm.Page);
        Assert.Equal(50, vm.PageSize);
        Assert.Equal("Code", vm.SortField);
        Assert.False(vm.Descending);
        Assert.Empty(vm.ListPreferences.Columns);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Cancel)]
    [InlineData(UnsavedChangesChoice.Discard)]
    public async Task Reset_respects_dirty_form_confirmation(UnsavedChangesChoice choice)
    {
        var store = SavedStore();
        var changes = new UnsavedChangesService { PromptAsync = () => Task.FromResult(choice) };
        using var vm = Create(store, changes);
        await vm.LoadAsync();
        await vm.NewCommand.ExecuteAsync(null);
        vm.EditName = "保留草稿";
        await vm.ResetListCommand.ExecuteAsync(null);
        if (choice == UnsavedChangesChoice.Cancel)
        {
            Assert.True(vm.IsDirty);
            Assert.Equal("保留草稿", vm.EditName);
            Assert.Equal("中文", vm.SearchText);
            Assert.Equal(100, store.Demo.PageSize);
            Assert.False(vm.ListPreferences.Columns["Remark"].Visible);
        }
        else
        {
            AssertDefaults(vm);
            Assert.False(vm.IsEditing);
            using var reopened = Create(new WorkspacePreferencesService(ConfigPath));
            await reopened.LoadAsync();
            AssertDefaults(reopened);
        }
    }

    [Fact]
    public async Task Failed_reset_write_keeps_committed_preferences_and_allows_retry()
    {
        var store = SavedStore();
        var original = File.ReadAllBytes(ConfigPath);
        using var vm = Create(store);
        await vm.LoadAsync();
        Directory.CreateDirectory(ConfigPath + ".tmp");
        await vm.ResetListCommand.ExecuteAsync(null);
        AssertDefaults(vm);
        Assert.True(vm.PreferencesSaveFailed);
        Assert.Equal("中文", store.Demo.SearchText);
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));
        Directory.Delete(ConfigPath + ".tmp");
        await vm.ResetListCommand.ExecuteAsync(null);
        Assert.False(vm.PreferencesSaveFailed);
        Assert.Empty(new WorkspacePreferencesService(ConfigPath).Demo.SearchText);
    }

    [AvaloniaFact]
    public async Task Pointer_resizing_a_column_is_saved_and_restored_in_a_new_view()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        using var vm = Create(store);
        await vm.LoadAsync();
        var view = new DemoListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 720 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var table = view.FindControl<TableView>("RecordsTable")!;
            Assert.True(table.CanUserResizeColumns);
            var before = table.Columns[0].ActualWidth;
            var handle = table.GetVisualDescendants().OfType<Thumb>()
                .First(thumb => thumb.Name == "PART_Resizer");
            Assert.True(handle.IsVisible && handle.IsHitTestVisible);
            var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            // Avalonia 12 TableView 只在分隔线左侧(即前一列表头内)按下才启动拖拽；
            // 按在 1px 分隔线本体或右侧是原生死区，实测不触发 DragStarted。
            var grab = new Point(start.X - 4, start.Y);
            window.MouseMove(grab);
            window.MouseDown(grab, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            for (var step = 1; step <= 6; step++)
            {
                window.MouseMove(new Point(grab.X + step * 10, grab.Y));
                Dispatcher.UIThread.RunJobs();
            }
            window.MouseUp(new Point(grab.X + 60, grab.Y), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var resized = table.Columns[0].ActualWidth;
            Assert.True(resized > before + 20, "Pointer drag must change the actual column width.");
            var saved = new WorkspacePreferencesService(ConfigPath);
            Assert.Equal(resized, saved.Demo.Columns["Code"].Width, 1);
            using var reopened = Create(saved);
            await reopened.LoadAsync();
            var reopenedView = new DemoListView { DataContext = reopened };
            window.Content = reopenedView;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(resized, reopenedView.FindControl<TableView>("RecordsTable")!.Columns[0].Width.Value, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Reset_button_restores_columns_toggles_and_detached_view()
    {
        var store = SavedStore();
        using var vm = Create(store);
        await vm.LoadAsync();
        var view = new DemoListView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var table = view.FindControl<TableView>("RecordsTable")!;
            Assert.False(table.Columns[2].IsVisible);
            Assert.Equal(350d, table.Columns[2].Width.Value);
            var button = view.FindControl<Button>("ResetListButton")!;
            Assert.Same(vm.ResetListCommand, button.Command);
            await vm.ResetListCommand.ExecuteAsync(button.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.All(table.Columns, column => Assert.True(column.IsVisible));
            Assert.Equal(new double[] { 140, 200, 200, 110, 130, 110, 80, 140, 120 },
                table.Columns.Select(column => column.Width.Value));
            Assert.All(view.FindControl<StackPanel>("ColumnMenu")!.Children.OfType<CheckBox>(),
                toggle => Assert.True(toggle.IsChecked == true));
            table.Columns[2].IsVisible = false;
            window.Content = null;
            await vm.ResetListCommand.ExecuteAsync(null);
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.True(table.Columns[2].IsVisible);
        }
        finally { window.Close(); }
    }
}
