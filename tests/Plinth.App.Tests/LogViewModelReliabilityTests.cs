using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

public sealed class LogViewModelReliabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_filters_and_refresh_contain_read_failures_without_logging(bool unexpected)
    {
        using var context = new ReliabilityTestContext();
        using var permissions = new FailingPermissions(unexpected);
        var sink = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Log.Logger = logger;
            using var vm = Create(permissions);
            CompleteFailure(permissions, context);
            AssertSafeFailure(vm);

            foreach (var trigger in new Action[] { () => vm.OnlyProblems = true,
                         () => vm.EventKindIndex = 1, vm.Refresh })
            {
                permissions.Arm();
                vm.Entries.Add(new LogEntry(DateTimeOffset.UtcNow, "Error", "old"));
                vm.SelectedEntry = vm.Entries[0];
                trigger();
                CompleteFailure(permissions, context);
                AssertSafeFailure(vm);
            }
            Assert.Empty(sink.Events);
        }
        finally { Log.Logger = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Late_failure_cannot_update_a_newer_query_or_disposed_page(bool dispose)
    {
        using var context = new ReliabilityTestContext();
        using var permissions = new FailingPermissions(false);
        using var vm = Create(permissions);
        permissions.ReleaseFailure();
        // Hold the already failed read's UI continuation until after the state changes.
        var late = context.Take();
        if (dispose)
            vm.Dispose();
        else
        {
            vm.From = DateTimeOffset.UtcNow;
            vm.To = vm.From.Value.AddDays(-1);
            Assert.True(vm.LoadAsync().IsCompletedSuccessfully);
        }
        vm.StatusText = "newer state";
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        context.Run(late);

        Assert.Equal("newer state", vm.StatusText);
        Assert.Empty(changes);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Entries);
    }

    private static LogViewModel Create(FailingPermissions permissions) =>
        new(new FolderService(NullLogger<FolderService>.Instance),
            new LogAccessService(permissions, authenticationEnabled: true));

    private static void CompleteFailure(FailingPermissions permissions, ReliabilityTestContext context)
    {
        permissions.ReleaseFailure();
        context.Run(context.Take());
    }

    private static void AssertSafeFailure(LogViewModel vm)
    {
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Entries);
        Assert.Null(vm.SelectedEntry);
        Assert.Null(vm.GetCopyText(false));
        Assert.Equal(Loc.Get("logs.status.failed"), vm.StatusText);
        Assert.DoesNotContain("private", vm.StatusText);
    }

    private sealed class FailingPermissions(bool unexpected) : IPermissionService, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        public bool HasPermission(string permission) => true;
        public void Demand(string permission, string correlationId)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (unexpected) throw new InvalidOperationException("private fault");
            throw new InvalidDataException("private oversized log");
        }
        public void Arm() { _entered.Reset(); _release.Reset(); }
        public void ReleaseFailure()
        {
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(5)));
            _release.Set();
        }
        public void Dispose() { _release.Set(); _entered.Dispose(); _release.Dispose(); }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}

// Explicitly deliver captured continuations; timeouts only bound a broken test, never order work.
internal sealed class ReliabilityTestContext : SynchronizationContext, IDisposable
{
    private readonly SynchronizationContext? _previous = Current;
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = new();
    public ReliabilityTestContext() => SetSynchronizationContext(this);
    public override void Post(SendOrPostCallback d, object? state) => _callbacks.Add((d, state));
    public (SendOrPostCallback Callback, object? State) Take()
    {
        Assert.True(_callbacks.TryTake(out var callback, TimeSpan.FromSeconds(5)));
        return callback;
    }
    public void Run((SendOrPostCallback Callback, object? State) callback) => callback.Callback(callback.State);
    public void Dispose()
    {
        SetSynchronizationContext(_previous);
        _callbacks.Dispose();
    }
}
