using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.Auth;
using Plinth.App.Remote;

namespace Plinth.App.ViewModels;

/// <summary>关于页:品牌与版本信息。</summary>
public partial class AboutViewModel(UpdateCheckService updates, BrandingService branding,
    ISystemBrowserLauncher? browser = null) : ViewModelBase, IDisposable
{
    private readonly ISystemBrowserLauncher _browser = browser ?? new SystemBrowserLauncher();
    private Uri? _updateDownload;
    private CancellationTokenSource? _check;
    private bool _disposed;
    public bool HasUpdateDownload => _updateDownload is not null;

    private void SetUpdateDownload(Uri? uri)
    {
        _updateDownload = uri;
        OnPropertyChanged(nameof(HasUpdateDownload));
        OpenUpdateDownloadCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenUpdateDownload))]
    private void OpenUpdateDownload()
    {
        if (!CanOpenUpdateDownload() || _updateDownload is not { } uri
            || !RemoteApiClient.IsHttpsAddress(uri)) return;
        try { _browser.Open(uri); }
        catch (Exception)
        {
            // Browser failures can contain machine paths or the full remote URL.
            Serilog.Log.Warning("Could not open the update download address.");
            UpdateStatus = Loc.Get("about.update.openFailed");
        }
    }

    private bool CanOpenUpdateDownload() => !_disposed && !IsChecking && HasUpdateDownload;

    public bool HasSupportUrl => branding.Current.SupportUrl.Length > 0;
    public Avalonia.Media.IImage? Logo => branding.Logo;
    public bool HasLogo => Logo is not null;

    [RelayCommand]
    private void OpenSupport()
    {
        // Revalidate at the boundary before passing anything to the OS shell.
        if (_disposed) return;
        var url = branding.Current.Normalize().SupportUrl;
        if (url.Length == 0) return;
        try
        {
            _browser.Open(new Uri(url));
        }
        catch (System.Exception) { UpdateStatus = Loc.Format("folder.failed", Loc.Get("about.support")); }
    }
    public string ProductName => branding.Current.ProductName;

    public string Description => branding.Current.Description;

    public string SupportText => branding.Current.SupportText;

    public string Version => AppInfo.Version;

    public string VersionText => Loc.Format("about.version", Version);

    public string Framework => AppInfo.FrameworkDescription;

    public string OperatingSystem => AppInfo.OsDescription;

    /// <summary>检查更新的结果文字;有新版本时包含下载地址。</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenUpdateDownloadCommand))]
    public partial bool IsChecking { get; set; }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckForUpdatesAsync()
    {
        if (!CanCheck()) return;
        using var request = new CancellationTokenSource();
        _check = request;
        IsChecking = true;
        SetUpdateDownload(null);
        UpdateStatus = Loc.Get("about.update.checking");
        try
        {
            var result = await updates.CheckAsync(request.Token);
            if (_disposed || request.IsCancellationRequested) return;
            if (result.Kind == UpdateCheckKind.Available
                && Uri.TryCreate(result.Url, UriKind.Absolute, out var uri)
                && RemoteApiClient.IsHttpsAddress(uri))
                SetUpdateDownload(uri);
            UpdateStatus = result.Kind switch
            {
                UpdateCheckKind.NotConfigured => Loc.Get("about.update.notConfigured"),
                UpdateCheckKind.UpToDate => Loc.Get("about.update.upToDate"),
                UpdateCheckKind.Available => Loc.Format("about.update.available", result.Version, result.Url)
                    + (result.Notes.Length > 0 ? "\n" + result.Notes : string.Empty),
                _ => Loc.Get("about.update.failed"),
            };
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed) return;
            Serilog.Log.Warning(ex, "Update check could not complete.");
            UpdateStatus = Loc.Get("about.update.failed");
        }
        finally
        {
            _check = null;
            if (!_disposed) IsChecking = false;
        }
    }

    private bool CanCheck() => !_disposed && !IsChecking;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _check?.Cancel();
        SetUpdateDownload(null);
        IsChecking = false;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }
}
