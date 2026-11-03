using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class SidePanelTests
{
    [AvaloniaFact]
    public void Hiding_panel_collapses_column_and_reopening_keeps_width()
    {
        using var shell = new Shell();
        var vm = shell.ViewModel;
        vm.ShowTaskPanelCommand.Execute(null);
        shell.Layout();
        var column = shell.ShellGrid.ColumnDefinitions[4];
        Assert.True(column.Width.Value >= 240);
        column.Width = new GridLength(350);
        shell.Layout();
        vm.HideSidePanelCommand.Execute(null);
        shell.Layout();
        Assert.Equal(0, column.Width.Value);
        Assert.Equal(0, column.MinWidth);
        vm.ShowTaskPanelCommand.Execute(null);
        shell.Layout();
        Assert.Equal(350, column.Width.Value);
        Assert.Equal(350, vm.SidePanelWidth);
        Assert.Equal(1, vm.SidePanelTab);
    }

    [AvaloniaFact]
    public void Compact_rail_centers_navigation_icons_horizontally_and_vertically()
    {
        using var shell = new Shell();
        shell.ViewModel.ToggleSidebarCompactCommand.Execute(null);
        shell.Layout();

        var list = shell.NavList;
        Assert.Equal(48, shell.ShellGrid.ColumnDefinitions[0].Width.Value);
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            var row = item.GetVisualDescendants().OfType<StackPanel>()
                .Single(panel => panel.Classes.Contains("navItemRow"));
            var icon = row.Children.OfType<TextBlock>().First();
            var itemOrigin = item.TranslatePoint(new Avalonia.Point(0, 0), shell.Window)!.Value;
            var iconOrigin = icon.TranslatePoint(new Avalonia.Point(0, 0), shell.Window)!.Value;

            var iconCenterX = iconOrigin.X + (icon.Bounds.Width / 2);
            var itemCenterX = itemOrigin.X + (item.Bounds.Width / 2);
            var iconCenterY = iconOrigin.Y + (icon.Bounds.Height / 2);
            var itemCenterY = itemOrigin.Y + (item.Bounds.Height / 2);
            Assert.InRange(Math.Abs(iconCenterX - itemCenterX), 0, 1);
            Assert.InRange(Math.Abs(iconCenterY - itemCenterY), 0, 1);
        }
    }

    [AvaloniaFact]
    public void Details_follow_active_page_and_detach_from_previous_page()
    {
        using var shell = new Shell();
        var vm = shell.ViewModel;
        var first = new DetailPage();
        var second = new DetailPage();
        var a = new WorkspaceTabViewModel("a", "A", first);
        var b = new WorkspaceTabViewModel("b", "B", second);
        vm.Tabs.Add(a);
        vm.Tabs.Add(b);
        vm.SelectedTab = a;
        first.SetValue("one");
        Assert.Equal("one", Assert.Single(vm.PageDetails).Value);
        var detailChanges = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.PageDetails)) detailChanges++; };
        vm.SelectedTab = b;
        var changes = detailChanges;
        first.SetValue("old page");
        Assert.Equal(changes, detailChanges);
        second.SetValue("two");
        Assert.True(detailChanges > changes);
        Assert.Equal("two", Assert.Single(vm.PageDetails).Value);
        vm.SelectedTab = null;
        Assert.False(vm.HasPageDetails);
    }

    [AvaloniaFact]
    public async Task Panel_cancels_running_task_and_opens_its_diagnostic_record()
    {
        using var shell = new Shell();
        var vm = shell.ViewModel;
        var tasks = shell.Provider.GetRequiredService<TaskService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.Enqueue("Import", "Demo", async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var activeTab = vm.SelectedTab;
            vm.ShowTaskPanelCommand.Execute(null);
            Assert.Same(activeTab, vm.SelectedTab);
            var row = vm.PanelTasks.Single(item => item.Id == id);
            Assert.True(row.CanCancel);
            vm.CancelPanelTaskCommand.Execute(row);
            await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            vm.RefreshPanelTasks();
            row = vm.PanelTasks.Single(item => item.Id == id);
            Assert.Equal(WorkStatus.Cancelled, row.Status);
            Assert.False(row.CanCancel);
            vm.OpenPanelTaskCommand.Execute(row);
            Assert.Equal(id, Assert.IsType<TaskDiagnosticsViewModel>(vm.SelectedTab!.Content).SelectedItem!.Id);
        }
        finally
        {
            tasks.Cancel(id);
            await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class DetailPage : ViewModelBase, IPageDetails
    {
        private string _value = "";
        public IReadOnlyList<PageDetail> Details => [new("Field", _value)];
        public void SetValue(string value)
        {
            _value = value;
            OnPropertyChanged(nameof(Details));
        }
    }
}
