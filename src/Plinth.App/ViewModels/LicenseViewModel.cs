using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

public partial class LicenseViewModel : ViewModelBase, IDisposable
{
    private readonly LicenseNoticeService _service;
    private CancellationTokenSource? _loadCancellation;
    private Task? _loadTask;
    private bool _disposed;

    public LicenseViewModel(LicenseNoticeService service) => _service = service;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string Text { get; private set; } = "";

    private bool CanLoad() => !_disposed && !IsBusy;

    // UI-thread entry point. Coalesce Loaded/command calls, including direct method calls.
    [RelayCommand(CanExecute = nameof(CanLoad))]
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (_loadTask is { IsCompleted: false } running)
        {
            // A view can reattach before its cancelled read has unwound.
            return _loadCancellation?.IsCancellationRequested == true
                ? ReloadAfterCancellationAsync(running, cancellationToken)
                : running;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCancellation = cancellation;
        IsBusy = true;
        Text = "";
        StatusText = Loc.Get("license.status.loading");
        return _loadTask = LoadCoreAsync(cancellation);
    }

    private async Task ReloadAfterCancellationAsync(Task previous, CancellationToken cancellationToken)
    {
        await previous;
        if (!_disposed && !cancellationToken.IsCancellationRequested)
            await LoadAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(CancellationTokenSource cancellation)
    {
        // Publish _loadTask before any synchronous completion/failure can clear state.
        await Task.Yield();
        try
        {
            var result = await _service.ReadAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            Text = result.Text;
            StatusText = Loc.Get(result.Status switch
            {
                LicenseNoticeStatus.Loaded => "license.status.loaded",
                LicenseNoticeStatus.Missing => "license.status.missing",
                LicenseNoticeStatus.TooLarge => "license.status.tooLarge",
                _ => "license.status.unreadable"
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed)
            {
                Text = "";
                StatusText = Loc.Get("license.status.cancelled");
            }
        }
        finally
        {
            _loadCancellation = null;
            if (!_disposed) IsBusy = false;
            cancellation.Dispose();
        }
    }

    public void PageClosed() => _loadCancellation?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PageClosed();
        Text = "";
        IsBusy = false;
        LoadCommand.NotifyCanExecuteChanged();
        // 活跃读取自己的 finally 负责释放 CTS。
    }
}
