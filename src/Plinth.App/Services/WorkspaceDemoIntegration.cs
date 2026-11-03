using System.Collections.Generic;

namespace Plinth.App.Services;

/// <summary>
/// 主代理接入清单，不在运行时注册资源。
/// DI: services.AddSingleton&lt;WorkspacePreferencesService&gt;();
///     services.AddSingleton&lt;UnsavedChangesService&gt;();
/// Shell 与 Demo 必须共用确认服务；配置服务未注册时不会持久化。
///
/// 宿主衔接：App 托盘的 desktop.Shutdown() 改为 mainWindow.Close()；
/// ShutdownRequested 中的 DisposeServices 移到已批准退出后的 Exit 或 Closed；
/// 系统退出的可取消请求需先取消，转入同一主窗口关闭路径。
///
/// 配置位于 AppPaths.DataDirectory/workspace-preferences.json。
/// 重置只恢复侧栏 260 DIP 与显隐，保留打开页面及编辑内容。
/// 批量关闭期间此前明确选择的保存/丢弃已经生效，不回滚已提交的数据。
/// 旧 SearchAsync 和 CSV 导入导出实现保持原样。
///
/// 验证：当前环境无 dotnet/csc/msbuild，未运行 .NET 测试。
/// 接入后执行 dotnet test，并在 Windows 11 完成规范 §6.4 的 IME/键鼠/DPI/大数据 POC。
/// </summary>
internal static class WorkspaceDemoIntegration
{
    /// <summary>需要主代理写入共享英文和中文 resx 的 key 与译文。</summary>
    internal static IReadOnlyDictionary<string, (string English, string Chinese)> Resources { get; } =
        new Dictionary<string, (string, string)>
        {
            ["shell.layout.reset"] = ("Reset workspace layout", "重置工作区布局"),
            ["shell.layout.saveFailed"] = ("Could not save workspace layout. Check data folder permissions.", "工作区布局保存失败，请检查数据目录权限。"),
            ["shell.unsaved.title"] = ("Unsaved changes", "未保存的更改"),
            ["shell.unsaved.message"] = ("Save changes to the current record?", "是否保存当前编辑的更改？"),
            ["shell.unsaved.save"] = ("Save", "保存"),
            ["shell.unsaved.discard"] = ("Discard", "丢弃"),
            ["shell.unsaved.cancel"] = ("Cancel", "取消"),
            ["shell.unsaved.failed"] = ("Could not check whether the workspace can close. Retry and check the logs.", "无法检查关闭条件，请重试并查看日志。"),
            ["demo.form.unsaved"] = ("Unsaved changes", "有未保存的更改"),
            ["demo.list.sort"] = ("Sort by", "排序"),
            ["demo.list.descending"] = ("Descending", "降序"),
            ["demo.list.copy"] = ("Copy selected record", "复制所选记录"),
            ["demo.list.columns"] = ("Visible columns", "显示列"),
            ["demo.list.previous"] = ("Previous", "上一页"),
            ["demo.list.next"] = ("Next", "下一页"),
            ["demo.list.pageSize"] = ("Rows per page", "每页条数"),
            ["demo.list.preferencesFailed"] = ("Could not save list preferences. Check data folder permissions.", "列表设置保存失败，请检查数据目录权限。"),
            ["demo.list.empty"] = ("No matching records", "没有匹配的记录"),
        };
}
