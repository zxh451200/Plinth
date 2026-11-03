using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>单实例(规范 §15.3):第二次启动能通知第一个实例;没有实例在运行时通知不会卡住或抛异常。</summary>
public class InstanceActivatorTests
{
    private static void NewId() =>
        InstanceActivator.TestInstanceId = "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Notifying_with_nobody_listening_returns_false_without_throwing()
    {
        NewId();
        Assert.False(InstanceActivator.TryNotifyRunningInstance());
    }

    [Fact]
    public async Task A_running_instance_is_told_when_someone_starts_a_second_one()
    {
        NewId();
        await using var first = new InstanceActivator();
        var activated = new TaskCompletionSource();
        first.ActivationRequested += () => activated.TrySetResult();
        first.StartListening();

        await NotifyRunningInstanceAsync();
        Assert.True(activated.Task.Wait(TimeSpan.FromSeconds(5)), "activation callback timed out");
    }

    [Fact]
    public async Task Dispose_returns_while_callback_is_blocked_and_StopAsync_waits_for_it()
    {
        NewId();
        var first = new InstanceActivator();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposing = null;
        first.ActivationRequested += () =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Wait();
        };
        first.StartListening();

        try
        {
            await NotifyRunningInstanceAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 独立线程排除线程池调度延迟;回调只有在 Dispose 返回并完成断言后才放行。
            disposing = Task.Factory.StartNew(() =>
            {
                disposeEntered.TrySetResult();
                first.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await disposing.WaitAsync(TimeSpan.FromSeconds(1));

            first.Dispose();
            var stopped = first.StopAsync();
            var disposedAsync = first.DisposeAsync().AsTask();
            Assert.False(stopped.IsCompleted);
            Assert.False(disposedAsync.IsCompleted);

            releaseCallback.Set();
            await Task.WhenAll(stopped, disposedAsync).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(InstanceActivator.TryNotifyRunningInstance());
        }
        finally
        {
            releaseCallback.Set();
            if (disposing is not null)
            {
                await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StopAsync_cancels_listening_and_repeated_start_and_stop_are_safe()
    {
        NewId();
        await using var first = new InstanceActivator();
        var activated = new TaskCompletionSource();
        first.ActivationRequested += () => activated.TrySetResult();
        first.StartListening();
        first.StartListening();
        await NotifyRunningInstanceAsync();
        Assert.True(activated.Task.Wait(TimeSpan.FromSeconds(5)), "activation callback timed out");

        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();
        await first.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(first.StartListening);
        Assert.False(InstanceActivator.TryNotifyRunningInstance());
    }

    [Fact]
    public async Task Disposing_before_start_is_idempotent_and_prevents_start()
    {
        NewId();
        var first = new InstanceActivator();
        first.Dispose();
        first.Dispose();

        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await first.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(first.StartListening);
    }

    [Fact]
    public async Task Disposing_immediately_after_start_does_not_race_token_access()
    {
        NewId();
        // 反复覆盖启动后立即取消的竞争窗口;每轮等待退出,避免管道跨轮残留。
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = new InstanceActivator();
            first.StartListening();
            first.Dispose();
            first.Dispose();
            await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Concurrent_disposal_is_idempotent()
    {
        NewId();
        await using var first = new InstanceActivator();
        first.StartListening();
        var disposals = new Task[16];
        for (var index = 0; index < disposals.Length; index++)
        {
            disposals[index] = Task.Run(first.Dispose);
        }

        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(first.StartListening);
    }

    [Fact]
    public async Task Activation_callback_can_request_disposal()
    {
        NewId();
        await using var first = new InstanceActivator();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += () =>
        {
            first.Dispose();
            disposed.TrySetResult();
        };
        first.StartListening();

        // 监听按现有约定在连接时触发回调,无需写入;避免回调关管道与客户端写入竞争。
        using var client = new NamedPipeClientStream(
            ".", $"Plinth.Activate.{InstanceActivator.TestInstanceId}", PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Listener_fault_is_observed_and_reported_by_async_shutdown()
    {
        NewId();
        var first = new InstanceActivator();
        var failure = new InvalidOperationException("Activation callback failed");
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += () =>
        {
            callbackEntered.TrySetResult();
            throw failure;
        };
        first.StartListening();

        try
        {
            using var client = new NamedPipeClientStream(
                ".", $"Plinth.Activate.{InstanceActivator.TestInstanceId}", PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(5000);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            first.Dispose();
            var fromStop = await Assert.ThrowsAsync<InvalidOperationException>(
                () => first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var fromDispose = await Assert.ThrowsAsync<InvalidOperationException>(
                () => first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, fromStop);
            Assert.Same(failure, fromDispose);
            first.Dispose();
        }
    }

    [Fact]
    public void Different_data_folders_are_different_instances()
    {
        NewId();
        Assert.NotEqual(InstanceActivator.ComputeInstanceId(@"C:\a"), InstanceActivator.ComputeInstanceId(@"C:\b"));
        Assert.Equal(InstanceActivator.ComputeInstanceId(@"C:\a"), InstanceActivator.ComputeInstanceId(@"c:\A"));
    }

    private static async Task NotifyRunningInstanceAsync()
    {
        // 监听刚启动时管道可能还没建好,保留原测试的重试策略。
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await Task.Run(InstanceActivator.TryNotifyRunningInstance))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Running instance did not accept an activation notification.");
    }
}
