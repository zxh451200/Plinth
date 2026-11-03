using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CommandMenuTests
{
    [Fact]
    public void Forwarder_tracks_can_execute_and_detaches_the_previous_command()
    {
        var enabled = true;
        var executions = 0;
        var first = new RelayCommand(() => executions++, () => enabled);
        var second = new RelayCommand(() => executions += 10);
        ICommand? current = first;
        using var menu = new CommandMenu("demo.export", () => current);
        var changes = 0;
        menu.CanExecuteChanged += (_, _) => changes++;
        menu.RefreshTarget();
        menu.Execute(null);
        Assert.Equal(1, executions);
        enabled = false;
        first.NotifyCanExecuteChanged();
        Assert.False(menu.CanExecute(null));
        menu.Execute(null);
        Assert.Equal(1, executions);
        current = second;
        menu.RefreshTarget();
        var before = changes;
        first.NotifyCanExecuteChanged();
        Assert.Equal(before, changes);
        menu.Execute(null);
        Assert.Equal(11, executions);
        current = null;
        menu.RefreshTarget();
        Assert.False(menu.CanExecute(null));
    }

    [AvaloniaFact]
    public void Contributions_only_target_the_active_existing_page()
    {
        using var shell = new Shell();
        var executions = 0;
        var page = new TestPage();
        var target = new RelayCommand(() => executions++);
        CommandMenuContribution[] contributions =
        [
            new("custom", "demo.export", content => ReferenceEquals(content, page) ? target : null),
        ];
        var vm = ActivatorUtilities.CreateInstance<MainViewModel>(shell.Provider, (object)contributions);
        var tab = new WorkspaceTabViewModel("custom", "Custom", page);
        vm.Tabs.Add(tab);
        vm.SelectedTab = tab;
        var command = Assert.Single(vm.ModuleCommands);
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, executions);
        vm.ShowAboutCommand.Execute(null);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, executions);
        vm.SelectTabCommand.Execute(tab);
        Assert.Same(tab, vm.SelectedTab);
        Assert.True(command.CanExecute(null));
        vm.ReleaseWorkspace();
    }

    [AvaloniaFact]
    public void Tools_open_registered_diagnostics_once_and_window_selection_reuses_tabs()
    {
        using var shell = new Shell();
        shell.ViewModel.ShowLogsCommand.Execute(null);
        var logs = shell.ViewModel.SelectedTab;
        Assert.Equal(ShellNavigation.LogsId, logs?.Id);
        shell.ViewModel.ShowLogsCommand.Execute(null);
        Assert.Same(logs, shell.ViewModel.SelectedTab);
        shell.ViewModel.ShowTaskDiagnosticsCommand.Execute(null);
        Assert.Equal(ShellNavigation.TaskDiagnosticsId, shell.ViewModel.SelectedTab?.Id);
        shell.ViewModel.SelectTabCommand.Execute(logs);
        Assert.Same(logs, shell.ViewModel.SelectedTab);
        Assert.Single(shell.ViewModel.Tabs.Where(tab => tab.Id == ShellNavigation.LogsId));
    }

    [AvaloniaFact]
    public async Task Shortcuts_preserve_text_delete_escape_and_dirty_confirmation()
    {
        using var shell = new Shell();
        using var database = new TestDatabase();
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
        await service.SaveAsync(new DemoRecord { Code = "A", Name = "First" });
        using var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance,
            shell.ViewModel.Changes);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        shell.ViewModel.Tabs.Add(new WorkspaceTabViewModel(DemoModuleRegistration.PageId, "Demo", vm));
        shell.ViewModel.SelectedTab = shell.ViewModel.Tabs.Last();
        shell.Layout();
        var search = shell.PageView<DemoListView>().FindControl<TextBox>("SearchBox")!;
        search.Focus();
        search.Text = "ABC";
        shell.Press(Key.A, PhysicalKey.A, RawInputModifiers.Control);
        Assert.Equal(0, search.SelectionStart);
        Assert.Equal(3, search.SelectionEnd);
        search.SelectionStart = search.SelectionEnd = search.CaretIndex = 1;
        shell.Press(Key.Delete, PhysicalKey.Delete);
        Assert.Equal("AC", search.Text);
        Assert.False(vm.IsConfirmingDelete);
        vm.EditName = "Unsaved";
        var prompts = 0;
        shell.ViewModel.Changes.PromptAsync = () =>
        {
            prompts++;
            return Task.FromResult(UnsavedChangesChoice.Cancel);
        };
        shell.Press(Key.Escape, PhysicalKey.Escape);
        Assert.Equal(0, prompts);
        shell.Press(Key.N, PhysicalKey.N, RawInputModifiers.Control);
        if (vm.NewCommand.ExecutionTask is { } creating) await creating;
        Assert.Equal(1, prompts);
        Assert.Equal("Unsaved", vm.EditName);
        shell.TabHost.Focusable = true;
        Assert.True(shell.TabHost.Focus());
        shell.Press(Key.Delete, PhysicalKey.Delete);
        Assert.True(vm.IsConfirmingDelete);
        shell.Press(Key.Escape, PhysicalKey.Escape);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Equal(1, prompts);
        shell.Press(Key.Escape, PhysicalKey.Escape);
        if (vm.CancelCommand.ExecutionTask is { } cancelling) await cancelling;
        Assert.Equal(2, prompts);
        Assert.Equal("Unsaved", vm.EditName);
        shell.Press(Key.F, PhysicalKey.F, RawInputModifiers.Control);
        Assert.True(search.IsFocused);
        Assert.Equal(0, search.SelectionStart);
        Assert.Equal(search.Text?.Length, search.SelectionEnd);
        shell.ViewModel.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        await vm.CancelCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Ctrl_n_and_ctrl_s_create_and_save_using_existing_commands()
    {
        using var shell = new Shell();
        using var database = new TestDatabase();
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
        using var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance,
            shell.ViewModel.Changes);
        await vm.LoadAsync();
        shell.ViewModel.Tabs.Add(new WorkspaceTabViewModel(DemoModuleRegistration.PageId, "Demo", vm));
        shell.ViewModel.SelectedTab = shell.ViewModel.Tabs.Last();
        shell.Layout();
        shell.PageView<DemoListView>().FindControl<TextBox>("SearchBox")!.Focus();
        shell.Press(Key.N, PhysicalKey.N, RawInputModifiers.Control);
        if (vm.NewCommand.ExecutionTask is { } creating) await creating;
        Assert.True(vm.IsEditing);
        vm.EditCode = "KEY";
        vm.EditName = "Saved from keyboard";
        shell.Press(Key.S, PhysicalKey.S, RawInputModifiers.Control);
        if (vm.SaveCommand.ExecutionTask is { } saving) await saving;
        Assert.False(vm.IsEditing);
        Assert.Equal("Saved from keyboard", (await service.SearchPageAsync("KEY")).Items.Single().Name);
    }

    private sealed class TestPage : ViewModelBase { }
}
