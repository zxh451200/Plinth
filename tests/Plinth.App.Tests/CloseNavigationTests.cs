using Avalonia.Headless.XUnit;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CloseNavigationTests
{
    [AvaloniaTheory]
    [InlineData("single")]
    [InlineData("all")]
    [InlineData("exit")]
    public async Task Rejected_close_selects_the_blocking_page_and_keeps_all_tabs(string operation)
    {
        using var shell = new Shell();
        var vm = shell.ViewModel;
        var blocked = new WorkspaceTabViewModel("test.blocked", "Blocked", new BlockedPage());
        vm.Tabs.Add(blocked);
        var original = vm.SelectedTab;
        Assert.NotSame(blocked, original);
        var count = vm.Tabs.Count;
        if (operation == "single") await vm.CloseTabItemCommand.ExecuteAsync(blocked);
        else if (operation == "all") await vm.CloseAllTabsCommand.ExecuteAsync(null);
        else Assert.False(await vm.PrepareExitAsync());
        Assert.Equal(count, vm.Tabs.Count);
        Assert.Same(blocked, vm.SelectedTab);
    }

    private sealed class BlockedPage : ViewModelBase, IUnsavedChanges
    {
        public bool IsDirty => true;
        public Task<bool> TryLeaveAsync() => Task.FromResult(false);
    }
}
