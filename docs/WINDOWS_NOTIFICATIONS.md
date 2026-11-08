# Windows 系统通知接入记录

核对日期：2026-11-04。状态：已接入并在 Windows 11 实测显示、点击链路与安装卸载清理；通知横幅的物理点击仍需人工复核。

任务结束后的系统通知属于 Should。接入位置是 `TaskService.Completed`，通知失败不能改变任务结果。通知内容只使用固定的本地化任务状态，不传业务正文、文件位置、异常或凭据；完整结果由应用内任务中心查看。

## 实现

- `TaskService.Completed` → `MainViewModel.OnTaskCompleted` → `TaskCompletionNotifier.TryNotify(status)`。只有生产入口 `Program` 打开 `Enabled`；测试不启动任何外部进程。
- 通知正文只有“任务已完成／任务失败／任务已取消”三种固定文案，标题固定为产品名；不读取任务标题、结果、路径或异常。
- 发送器是随包脚本 `notify.ps1`，由系统自带的 Windows PowerShell 5.1 调用 `Windows.UI.Notifications` 的 WinRT API。应用以隐藏窗口异步启动它，启动失败只写调试日志，不改变任务终态。
- 选择 Inbox WinRT + 随包脚本，是因为工程保持单一 `net10.0` 目标并让 Linux 观察轨继续构建；引入 Windows App SDK 需要多目标框架、额外运行时包、许可／SBOM 和自包含部署变更，收益仅限于编译期类型检查。
- 应用标识 AUMID 为 `Plinth`。安装器写入 `HKCU\Software\Classes\AppUserModelId\Plinth`；便携包首次通知时由脚本幂等自建同一项，不改用户的通知开关。
- 点击通知走 `activationType="protocol"` + `launch="plinth://tasks"`。安装器注册 `HKCU\Software\Classes\plinth`，命令为 `"<安装目录>\Plinth.App.exe" "plinth://tasks"`；卸载时只有在命令仍指向本安装目录时才删除该协议项。
- `Program` 只识别这一个固定 URI：冷启动进入任务中心；已有实例通过命名管道的 1 字节意图同样进入任务中心；其它参数仍按原规则拒绝，不把外部输入当文件或命令执行。

## 实测证据

- 显示：`artifacts/acceptance/toast-history.txt`。脚本退出 0，Windows 通知历史从 2 增至 3，`CreateToastNotifier("Plinth").Setting` 为 `Enabled`，历史中的 XML 为固定标题、`任务已完成` 和 `launch="plinth://tasks"`。
- 冷启动与已有实例：`artifacts/acceptance/protocol-warm.json`。普通启动停在“概览”；第二次以 `plinth://tasks` 启动退出 0，已有窗口切到“任务与诊断”；另一次以 URI 冷启动直接进入“任务与诊断”。
- 安装与卸载：`artifacts/acceptance/installer-final.json`。安装后 `notify.ps1`、锁文件、AUMID 与协议项均在；`--version` 正常；卸载后安装目录、AUMID、协议项移除，用户数据库保留。
- 自动回归：`TaskCompletionNotifierTests`（参数只含固定状态、缺脚本安全降级）、`StartupArgumentsTests`、`TaskCenterEntryTests`、`InstanceActivatorTests` 的意图路由与旧客户端兼容用例，全部通过。

## 仍需人工或环境才能关闭

1. 物理点击通知横幅：自动化没有点击系统通知横幅，协议目标本身已用冷启动和已有实例验证；点击只是触发同一协议。
2. 横幅可见性：本次截图时前台是全屏窗口，通知进入通知中心但未捕获横幅；焦点助手／勿扰／通知关闭策略需要人工确认。
3. 读屏朗读通知、通知关闭与免打扰设置下的体验。
4. 被策略禁用 PowerShell 或脚本执行的机器上的降级体验（代码会静默跳过并写调试日志，本轮未在禁用策略镜像验证）。
5. 多数据目录：通知归属跟随产生任务的实例；AUMID 与协议是当前用户的同一入口，点击会激活默认数据目录的实例。多实例下的点击归属仍需按实际部署方式人工验收。

## 上游依据

- [微软：在 .NET 应用中使用应用通知](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
- [微软：AppNotificationManager.Register](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.register?view=windows-app-sdk-2.0)
- [微软：ToastNotificationManager](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager)
- [DesktopNotifications 原仓库](https://github.com/pr8x/DesktopNotifications)：核对时已标记于 2026-01-12 归档，不作为新的默认依赖。
