using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

public sealed class HelpTests
{
    [Fact]
    public void Search_covers_body_key_and_error_code_and_clears_stale_selection()
    {
        var vm = new HelpViewModel(
        [
            new("start", "Start", "Workspace basics"),
            new("module.edit", "Editing", "保存失败 keep input", "module.page"),
            new("errors", "Errors", "Inspect correlation ID", ErrorCodes: ["TASK_FAILED"]),
        ]);
        vm.SearchText = "保存";
        Assert.Equal("module.edit", Assert.Single(vm.Topics).Key);
        Assert.Equal("module.edit", vm.SelectedTopic!.Key);
        vm.SearchText = "task_failed";
        Assert.Equal("errors", Assert.Single(vm.Topics).Key);
        vm.SearchText = "no matching topic";
        Assert.False(vm.HasTopics);
        Assert.Null(vm.SelectedTopic);
        vm.ShowForPage("module.page");
        Assert.Equal("", vm.SearchText);
        Assert.Equal(3, vm.Topics.Count);
        Assert.Equal("module.edit", vm.SelectedTopic!.Key);
        vm.ShowTopic("TASK_FAILED");
        Assert.Equal("errors", vm.SelectedTopic!.Key);
        vm.ShowTopic("unknown");
        Assert.Equal("start", vm.SelectedTopic!.Key);
    }

    [Fact]
    public void Duplicate_keys_fail_and_empty_catalog_is_safe()
    {
        Assert.Throws<InvalidOperationException>(() => new HelpViewModel(
            [new("same", "A", "a"), new("same", "B", "b")]));
        var empty = new HelpViewModel([]);
        empty.ShowTopic("missing");
        Assert.Empty(empty.Topics);
        Assert.Null(empty.SelectedTopic);
    }

    [AvaloniaFact]
    public void F1_uses_registered_module_topic_and_reuses_help_tab()
    {
        using var shell = new Shell();
        var notes = shell.ViewModel.NavigationItems.Single(item => item.Id == "notes.editor");
        shell.ViewModel.OpenTabCommand.Execute(notes);
        shell.Press(Key.F1, PhysicalKey.F1);
        var help = Assert.IsType<HelpViewModel>(shell.ViewModel.SelectedTab!.Content);
        Assert.Equal("notes.editing", help.SelectedTopic!.Key);
        shell.Press(Key.F1, PhysicalKey.F1);
        Assert.Same(help, shell.ViewModel.SelectedTab.Content);
        Assert.Equal("notes.editing", help.SelectedTopic.Key);
        Assert.Single(shell.ViewModel.Tabs.Where(tab => tab.Id == ShellNavigation.HelpId));
        shell.ViewModel.ShowHelpCommand.Execute("TASK_FAILED");
        Assert.Equal("diagnostics", help.SelectedTopic.Key);
        Assert.IsType<HelpView>(shell.PageView<HelpView>());
    }

    [AvaloniaFact]
    public void License_entry_is_registered_as_a_reusable_hidden_page()
    {
        using var shell = new Shell();
        Assert.DoesNotContain(shell.ViewModel.MenuItems, item => item.Id == ShellNavigation.LicensesId);
        shell.ViewModel.ShowLicensesCommand.Execute(null);
        shell.ViewModel.ShowLicensesCommand.Execute(null);
        Assert.Single(shell.ViewModel.Tabs.Where(tab => tab.Id == ShellNavigation.LicensesId));
        Assert.IsType<LicenseViewModel>(shell.ViewModel.SelectedTab!.Content);
        shell.Layout();
        Assert.IsType<LicenseView>(shell.PageView<LicenseView>());
    }
}
