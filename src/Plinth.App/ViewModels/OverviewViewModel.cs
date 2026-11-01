using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Navigation;
using Plinth.App.Services;

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
    private void OpenDataFolder() => OpenFolder(AppPaths.DataDirectory, "数据目录");

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.LogDirectory, "日志目录");

    private void OpenFolder(string path, string displayName) =>
        ActionHint = _folderService.TryOpen(path) ? $"已打开{displayName}" : $"无法打开{displayName},请检查日志";

    public void Refresh() => LogSummary = ReadLogSummary();

    private static string ReadLogSummary()
    {
        try
        {
            var directory = new DirectoryInfo(AppPaths.LogDirectory);
            if (!directory.Exists)
            {
                return "日志目录尚未创建";
            }

            var files = directory.GetFiles("*.ndjson");
            if (files.Length == 0)
            {
                return "暂无日志文件";
            }

            var totalBytes = files.Sum(file => file.Length);
            return $"{files.Length} 个文件,共 {FormatSize(totalBytes)}";
        }
        catch (IOException)
        {
            return "无法读取日志目录";
        }
        catch (UnauthorizedAccessException)
        {
            return "无权限读取日志目录";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):N1} MB",
    };
}
