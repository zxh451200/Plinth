using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.Localization;

namespace Plinth.App.ViewModels;

/// <summary>概览页:显示真实运行状态(版本、运行时、目录、日志统计),同时作为页面样板。</summary>
public partial class OverviewViewModel : ViewModelBase, IRefreshable
{
    private readonly FolderService _folderService;

    public OverviewViewModel(FolderService folderService)
    {
        _folderService = folderService;
        LogSummary = ReadLogSummary();
    }

    public string Version => AppInfo.Version;

    public string Framework => AppInfo.FrameworkDescription;

    public string OperatingSystem => AppInfo.OsDescription;

    public string Architecture => AppInfo.ProcessArchitecture;

    public string DataDirectory => AppPaths.DataDirectory;

    public string LogDirectory => AppPaths.LogDirectory;

    [ObservableProperty]
    public partial string LogSummary { get; set; }

    /// <summary>页面自己的操作反馈;为空时不占位。</summary>
    [ObservableProperty]
    public partial string ActionHint { get; set; } = string.Empty;

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.DataDirectory, Loc.Get("folder.data"));

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.LogDirectory, Loc.Get("folder.log"));

    private void OpenFolder(string path, string displayName) =>
        ActionHint = _folderService.OpenWithMessage(path, displayName);

    public void Refresh() => LogSummary = ReadLogSummary();

    private static string ReadLogSummary()
    {
        try
        {
            var directory = new DirectoryInfo(AppPaths.LogDirectory);
            if (!directory.Exists)
            {
                return Loc.Get("overview.log.noFolder");
            }

            var files = directory.GetFiles("*.ndjson");
            if (files.Length == 0)
            {
                return Loc.Get("overview.log.empty");
            }

            var totalBytes = files.Sum(file => file.Length);
            return Loc.Format("overview.log.summary", files.Length, FormatSize(totalBytes));
        }
        catch (IOException)
        {
            return Loc.Get("overview.log.ioError");
        }
        catch (UnauthorizedAccessException)
        {
            return Loc.Get("overview.log.denied");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):N1} MB",
    };
}
