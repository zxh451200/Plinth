using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoContextMenuTests
{
    [AvaloniaFact]
    public async Task Right_click_targets_the_hit_row_and_delete_still_requires_confirmation()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.RightClick(page.Row("B"));
        Assert.True(page.Menu.IsOpen);
        Assert.Equal("B", page.Vm.SelectedItem!.Code);
        Assert.Same(page.Vm, page.Menu.DataContext);
        var delete = Assert.IsType<MenuItem>(page.Menu.Items[3]);
        Assert.Same(page.Vm.AskDeleteCommand, delete.Command);
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(page.Vm.IsConfirmingDelete);
        Assert.Equal(2, (await page.Service.SearchAsync("")).Count);
        page.Menu.Close();
        await page.Vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal("A", Assert.Single(await page.Service.SearchAsync("")).Code);
    }

    [AvaloniaFact]
    public async Task Failed_async_row_confirmation_is_reported_without_escaping_the_event_and_can_be_retried()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Vm.EditName = "Unsaved";
        var response = new TaskCompletionSource<UnsavedChangesChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Changes.PromptAsync = () => response.Task;
        var sink = new ErrorSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previous = Log.Logger;
        var escaped = 0;
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
        {
            escaped++;
            args.Handled = true;
        }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        Log.Logger = logger;
        try
        {
            page.Layout();
            page.RightClick(page.Row("B"));
            Assert.False(page.Menu.IsOpen);
            response.SetException(new InvalidOperationException("synthetic prompt failure"));
            await sink.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, escaped);
            Assert.False(page.Menu.IsOpen);
            Assert.Equal("A", page.Vm.SelectedItem!.Code);
            Assert.Equal("Unsaved", page.Vm.EditName);
            page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
            page.RightClick(page.Row("B"));
            Assert.True(page.Menu.IsOpen);
            Assert.Equal("B", page.Vm.SelectedItem!.Code);
        }
        finally
        {
            Log.Logger = previous;
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
        }
    }

    private sealed class ErrorSink : ILogEventSink
    {
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.MessageTemplate.Text.StartsWith("UI unhandled error.", StringComparison.Ordinal))
                Reported.TrySetResult();
        }
    }

    [AvaloniaFact]
    public async Task Cancelled_row_switch_keeps_input_and_does_not_open_menu_for_the_old_row()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Vm.EditName = "Unsaved";
        var prompts = 0;
        page.Changes.PromptAsync = () =>
        {
            prompts++;
            return Task.FromResult(UnsavedChangesChoice.Cancel);
        };
        page.Layout();
        page.RightClick(page.Row("B"));
        Assert.Equal(1, prompts);
        Assert.False(page.Menu.IsOpen);
        Assert.Equal("A", page.Vm.SelectedItem!.Code);
        Assert.Equal("Unsaved", page.Vm.EditName);
        Assert.False(page.Vm.IsConfirmingDelete);

        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        page.RightClick(page.Row("B"));
        Assert.True(page.Menu.IsOpen);
        Assert.Equal("B", page.Vm.SelectedItem!.Code);
        Assert.False(page.Vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Pending_prompt_disables_record_actions_and_does_not_queue_another_menu()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Vm.EditName = "Unsaved";
        var response = new TaskCompletionSource<UnsavedChangesChoice>();
        var finished = new TaskCompletionSource();
        page.Changes.PromptAsync = async () =>
        {
            var choice = await response.Task;
            finished.SetResult();
            return choice;
        };
        page.Layout();
        page.RightClick(page.Row("B"));
        Assert.False(page.Vm.CanUseSelectedRecord);
        page.Vm.AskDeleteCommand.Execute(null);
        Assert.False(page.Vm.IsConfirmingDelete);
        page.RightClick(page.Row("A"));
        response.SetResult(UnsavedChangesChoice.Cancel);
        await finished.Task;
        Dispatcher.UIThread.RunJobs();
        Assert.False(page.Menu.IsOpen);
        Assert.Equal("A", page.Vm.SelectedItem!.Code);
        Assert.Equal("Unsaved", page.Vm.EditName);
        Assert.True(page.Vm.CanUseSelectedRecord);
    }

    [AvaloniaFact]
    public async Task Authenticated_mode_without_grants_disables_edit_and_delete_but_keeps_copy_available()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        var authentication = DeploymentConfiguration.Current.Authentication;
        var enabled = authentication.Enabled;
        try
        {
            authentication.Enabled = true;
            page.Layout();
            page.RightClick(page.Row("A"));
            Assert.True(page.Menu.IsOpen);
            Assert.False(Assert.IsType<MenuItem>(page.Menu.Items[0]).IsEnabled);
            Assert.True(Assert.IsType<MenuItem>(page.Menu.Items[1]).IsEffectivelyEnabled);
            Assert.False(Assert.IsType<MenuItem>(page.Menu.Items[3]).IsEnabled);
            page.Vm.AskDeleteCommand.Execute(null);
            Assert.False(page.Vm.IsConfirmingDelete);
        }
        finally { authentication.Enabled = enabled; }
    }

    [AvaloniaFact]
    public async Task Replacing_view_model_closes_menu_and_clears_old_command_context()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.RightClick(page.Row("A"));
        Assert.True(page.Menu.IsOpen);
        page.View.DataContext = null;
        Assert.False(page.Menu.IsOpen);
        Assert.Null(page.Menu.DataContext);
        Assert.False(page.Vm.IsConfirmingDelete);
    }

    [AvaloniaFact]
    public async Task Save_before_switch_uses_a_current_record_and_a_live_menu_target()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Vm.EditName = "Saved";
        var response = new TaskCompletionSource<UnsavedChangesChoice>();
        page.Changes.PromptAsync = () => response.Task;
        page.Layout();
        var opened = new TaskCompletionSource();
        page.Menu.Opened += (_, _) => opened.TrySetResult();
        page.RightClick(page.Row("B"));
        response.SetResult(UnsavedChangesChoice.Save);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.Layout();
        Assert.True(page.Menu.IsOpen);
        Assert.NotNull(TopLevel.GetTopLevel(page.Menu));
        Assert.Same(page.Vm.Items.Single(item => item.Code == "B"), page.Vm.SelectedItem);
        Assert.Equal("Saved", (await page.Service.SearchAsync("A")).Single().Name);
        Assert.NotEmpty(page.Menu.GetVisualChildren());
    }

    [AvaloniaFact]
    public async Task Blank_area_has_no_stale_target_and_keyboard_menu_uses_the_selection()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.RightClick(page.Table, new Point(20, page.Table.Bounds.Height - 10));
        Assert.False(page.Menu.IsOpen);
        Assert.Equal("A", page.Vm.SelectedItem!.Code);

        page.Table.Focus();
        page.Window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        page.Window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(page.Menu.IsOpen);
        Assert.Equal("A", page.Vm.SelectedItem.Code);
        page.Vm.IsSaving = true;
        Assert.False(page.Menu.IsEnabled);
        page.Vm.IsSaving = false;
        Assert.True(page.Menu.IsEnabled);
    }

    private sealed class Page : IDisposable
    {
        private readonly TestDatabase _database = new();
        public UnsavedChangesService Changes { get; } = new();
        public DemoService Service { get; }
        public DemoListViewModel Vm { get; }
        public DemoListView View { get; }
        public Window Window { get; }
        public TableView Table => View.FindControl<TableView>("RecordsTable")!;
        public ContextMenu Menu => (ContextMenu)View.Resources["RecordContextMenu"]!;

        private Page()
        {
            Service = new(_database.Factory, NullLogger<DemoService>.Instance);
            Vm = new(Service, NullLogger<DemoListViewModel>.Instance, Changes);
            View = new DemoListView { DataContext = Vm };
            Window = new Window { Content = View, Width = 1200, Height = 720 };
        }

        public static async Task<Page> CreateAsync()
        {
            var page = new Page();
            try
            {
                await page.Service.SaveAsync(new DemoRecord { Code = "A", Name = "First" });
                await page.Service.SaveAsync(new DemoRecord { Code = "B", Name = "Second" });
                await page.Vm.LoadAsync();
                page.Window.Show();
                page.Layout();
                return page;
            }
            catch
            {
                page.Dispose();
                throw;
            }
        }

        public TableViewRow Row(string code) => Table.GetVisualDescendants().OfType<TableViewRow>()
            .Single(row => row.DataContext is DemoRecord record && record.Code == code);

        public void Layout()
        {
            Dispatcher.UIThread.RunJobs();
            Window.Measure(new Size(1200, 720));
            Window.Arrange(new Rect(0, 0, 1200, 720));
            Dispatcher.UIThread.RunJobs();
        }

        public void RightClick(Control target, Point? point = null)
        {
            var position = target.TranslatePoint(point ?? new Point(20, target.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(position);
            Window.MouseDown(position, MouseButton.Right);
            Window.MouseUp(position, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Menu.Close();
            Window.Close();
            Vm.Dispose();
            _database.Dispose();
        }
    }
}
