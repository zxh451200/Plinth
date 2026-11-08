using System;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Services;

/// <summary>
/// 默认只允许同时运行一个程序(规范 §15.3),避免两个进程同时写同一个 SQLite 数据库。
/// 第二次启动时,它通过本机的命名管道通知第一个程序"有人又启动了你",第一个程序把窗口带到前台,第二个直接退出。
/// 实例标识由数据目录算出来:数据目录不同(例如测试、便携使用),就算不同的实例,互不干扰。
/// </summary>
public sealed class InstanceActivator : IDisposable, IAsyncDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly string _instanceId;
    private CancellationTokenSource? _stop;
    private Task? _listener;
    private Task _listenerCleanup = Task.CompletedTask;
    private bool _disposed;
    private NamedPipeServerStream? _currentServer;

    public InstanceActivator() : this(TestInstanceId ?? InstanceId) { }

    /// <summary>测试用:每个用例独立实例标识,避免共享管道干扰。</summary>
    internal static string? TestInstanceId { get; set; }

    internal InstanceActivator(string instanceId) => _instanceId = instanceId;

    /// <summary>当前数据目录对应的实例标识(只含字母数字,可以放进互斥体和管道名)。</summary>
    public static string InstanceId { get; } = ComputeInstanceId(AppPaths.DataDirectory);

    private string PipeName => $"Plinth.Activate.{_instanceId}";

    /// <summary>别的实例要求激活本实例时触发(在后台线程触发,订阅者自己回到界面线程)。</summary>
    public event Action<ActivationIntent>? ActivationRequested;

    /// <summary>通知已经在运行的实例。通知成功返回 true;连不上(没有实例在监听)返回 false。</summary>
    public static bool TryNotifyRunningInstance() => TryNotifyRunningInstance(ActivationIntent.Show);

    /// <summary>通知已有实例并带上受控入口(普通启动或系统通知点击)。</summary>
    public static bool TryNotifyRunningInstance(ActivationIntent intent) =>
        TryNotifyRunningInstance(TestInstanceId ?? InstanceId, intent);

    internal static bool TryNotifyRunningInstance(string instanceId, ActivationIntent intent = ActivationIntent.Show)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"Plinth.Activate.{instanceId}",
                PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            // 服务端读完一个字节才触发回调;写入失败不算通知失败,连接本身已经唤醒监听。
            try
            {
                client.WriteByte((byte)(intent == ActivationIntent.TaskCenter ? 1 : 0));
                client.Flush();
            }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException
            or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>开始在后台等待别的实例的通知。重复调用无效;停止后调用抛出 ObjectDisposedException。</summary>
    public void StartListening()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
            {
                return;
            }

            var stop = new CancellationTokenSource();
            _stop = stop;
            // 必须在排入线程池之前取 token,不能让后台任务和 Dispose 争用 CTS.Token。
            var token = stop.Token;
            // 第一根管道同步建好:StartListening 返回后第二次启动就能连上,不依赖线程池调度。
            var first = CreateServer();
            _currentServer = first;
            // 监听是进程级长任务；专用线程同步等待连接，不依赖线程池调度。
            _listener = Task.Factory.StartNew(() => ListenBlocking(first, token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _listenerCleanup = _listener.ContinueWith(completed =>
            {
                // 即使宿主只调用同步 Dispose,也要观察故障;StopAsync 仍会向等待者传播原异常。
                _ = completed.Exception;
                lock (_lifetimeGate)
                {
                    stop.Dispose();
                    _stop = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>只请求停止,不等待监听或正在执行的激活回调。CTS 由监听完成后的延续释放。</summary>
    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // 与完成延续共用短锁,避免 Cancel 和 CTS.Dispose 并发;锁内不等待监听。
            _stop?.Cancel();
            // 关闭当前管道以唤醒专用线程上的同步 WaitForConnection。
            try { _currentServer?.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>请求停止并等待监听、回调及资源清理完成。监听故障会向调用方传播。</summary>
    public async Task StopAsync()
    {
        Dispose();
        await _listenerCleanup.ConfigureAwait(false);
        if (_listener is { } listener)
        {
            await listener.ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    internal static string ComputeInstanceId(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    private NamedPipeServerStream CreateServer() =>
        new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private void ListenBlocking(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                server.WaitForConnection();
                if (cancellationToken.IsCancellationRequested) return;
                var intent = ActivationIntent.Show;
                try
                {
                    // 旧客户端连接后不写字节：短暂等待后按普通激活处理。
                    // 管道是异步流，不支持 ReadTimeout，用带超时的 ReadAsync 同步等待。
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                    var buffer = new byte[1];
                    var read = server.ReadAsync(buffer, 0, 1, timeout.Token).GetAwaiter().GetResult();
                    if (read == 1 && buffer[0] == 1)
                    {
                        intent = ActivationIntent.TaskCenter;
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException)
                {
                }

                ActivationRequested?.Invoke(intent);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (cancellationToken.WaitHandle.WaitOne(500)) return;
            }
            finally
            {
                try { server.Dispose(); } catch (ObjectDisposedException) { }
            }

            if (cancellationToken.IsCancellationRequested) return;
            server = CreateServer();
            lock (_lifetimeGate) _currentServer = server;
        }
    }
}
