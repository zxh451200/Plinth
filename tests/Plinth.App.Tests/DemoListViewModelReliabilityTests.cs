using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoListViewModelReliabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row_selection_observes_synchronous_and_asynchronous_prompt_faults_and_can_retry(bool delayed)
    {
        using var database = new TestDatabase();
        var changes = new UnsavedChangesService();
        using var vm = await CreateAsync(database, changes);
        using var context = new ReliabilityTestContext();
        var old = vm.SelectedItem;
        var target = vm.Items[1];
        var failure = new InvalidOperationException("private prompt fault");
        var response = new TaskCompletionSource<UnsavedChangesChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        changes.PromptAsync = delayed ? () => response.Task : () => throw failure;
        var sink = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Log.Logger = logger;
            vm.SelectedItem = target;
            Assert.Same(old, vm.SelectedItem);
            AssertDraft(vm);
            if (delayed)
            {
                Assert.Empty(sink.Events);
                response.SetException(failure);
                context.Run(context.Take());
            }

            var reported = Assert.Single(sink.Events);
            Assert.Same(failure, reported.Exception);
            Assert.Equal("UI_UNEXPECTED", ((ScalarValue)reported.Properties["ErrorCode"]).Value);
            Assert.DoesNotContain("private", reported.RenderMessage());
            Assert.Same(old, vm.SelectedItem);
            AssertDraft(vm);
            Assert.Empty(vm.ErrorMessage);
            changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
            vm.SelectedItem = target;
            Assert.Same(target, vm.SelectedItem);
            Assert.Equal(target.Name, vm.EditName);
            Assert.False(vm.IsDirty);
            Assert.True(vm.CanUseSelectedRecord);
        }
        finally { Log.Logger = previous; }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task Late_prompt_completion_after_dispose_and_cancellation_do_not_report_or_change_state(
        bool dispose, int completion)
    {
        using var database = new TestDatabase();
        var changes = new UnsavedChangesService();
        using var vm = await CreateAsync(database, changes);
        using var context = new ReliabilityTestContext();
        var response = new TaskCompletionSource<UnsavedChangesChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        changes.PromptAsync = () => response.Task;
        var old = vm.SelectedItem;
        var sink = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Log.Logger = logger;
            vm.SelectedItem = vm.Items[1];
            if (dispose) vm.Dispose();
            var notifications = new List<string?>();
            vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            if (completion == 1) response.SetCanceled();
            else if (completion == 2) response.SetException(new InvalidOperationException("late fault"));
            else if (completion == 3) response.SetResult(UnsavedChangesChoice.Discard);
            else response.SetResult(UnsavedChangesChoice.Cancel);
            context.Run(context.Take());

            Assert.Empty(sink.Events);
            Assert.Same(old, vm.SelectedItem);
            AssertDraft(vm);
            if (dispose) Assert.Empty(notifications);
            else Assert.True(vm.CanUseSelectedRecord);
        }
        finally { Log.Logger = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_selection_still_propagates_prompt_exceptions(bool cancel)
    {
        using var database = new TestDatabase();
        var changes = new UnsavedChangesService();
        using var vm = await CreateAsync(database, changes);
        var old = vm.SelectedItem;
        Exception failure = cancel ? new OperationCanceledException() : new InvalidOperationException();
        changes.PromptAsync = () => Task.FromException<UnsavedChangesChoice>(failure);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => vm.SelectRecordAsync(vm.Items[1]));

        Assert.Same(failure, actual);
        Assert.Same(old, vm.SelectedItem);
        AssertDraft(vm);
        changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        Assert.True(await vm.SelectRecordAsync(vm.Items[1]));
    }

    private static async Task<DemoListViewModel> CreateAsync(TestDatabase database, UnsavedChangesService changes)
    {
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
        await service.SaveAsync(new DemoRecord { Code = "A1", Name = "First" });
        await service.SaveAsync(new DemoRecord { Code = "A2", Name = "Second" });
        var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance, changes);
        await vm.LoadAsync();
        Assert.True(await vm.SelectRecordAsync(vm.Items[0]));
        vm.EditName = "draft";
        vm.EditRemark = "unsaved remark";
        return vm;
    }

    private static void AssertDraft(DemoListViewModel vm)
    {
        Assert.True(vm.IsEditing);
        Assert.True(vm.IsDirty);
        Assert.Equal("draft", vm.EditName);
        Assert.Equal("unsaved remark", vm.EditRemark);
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
