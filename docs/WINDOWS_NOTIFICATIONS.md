# Windows 系统通知接入记录

核对日期：2026-11-02。状态：方案已核对，尚未接入或完成 Windows 验收。

任务结束后的系统通知属于 Should。接入位置是 `TaskService.Completed`，通知失败不能改变任务结果。通知内容只使用固定的本地化任务状态，不传业务正文、文件位置、异常或凭据；完整结果由应用内任务中心查看。

## 采用方向

优先验证微软 Windows App SDK 的 `AppNotificationManager`。官方 .NET 指南要求先配置 Windows Runtime API 与 Windows App SDK；当前工程统一使用 `net10.0`，不能仅添加命名空间便宣称可用。

传统 NSIS 安装属于 unpackaged 应用。官方说明该模式下 `Register()` 可自动完成 COM 注册，无需先改成 MSIX，也不应自行假定必须手写 AUMID。必须在 `Register()` 前订阅 `NotificationInvoked`，退出时调用 `Unregister()`。永久注册清理需按选定版本的卸载 API 验证，不能把进程退出退订当作卸载清理。

Windows App SDK 文档另提示，AppNotificationManager 依赖额外运行时包，self-contained 应用也需要核对该依赖。Plinth 的 self-contained .NET 发布不代表已经包含 Windows App Runtime 的全部通知能力。实际 bootstrap、部署和卸载行为必须用选定稳定版本验证。

通知激活还需要与现有命令行解析及单实例唤醒协作：冷启动和已有进程均只能进入受控的任务中心入口，不能把通知参数直接当文件或命令执行。不得为方便接入而放开未知命令行参数。多数据目录实例需要独立处理通知归属，不能显示另一个实例的数据。

## 接入前仍缺的证据

1. 在可联网的指定 SDK 环境选择受支持稳定版，真实还原 Windows App SDK 及投影，验证普通构建、Windows 发布和测试项目的目标框架兼容性。
2. 核对直接／传递依赖和 Windows App Runtime 的完整许可证、分发要求、锁文件与最终 SBOM。
3. 在 Windows 11 的 NSIS 与便携包上验证运行时初始化、注册、显示、通知点击、进程退出和升级卸载清理。
4. 验证通知关闭、免打扰、注册失败及运行时缺失时安全降级；不反复弹错，不修改任务终态，不主动修改用户通知设置。

当前宿主没有 dotnet、PowerShell 或 Windows 桌面；固定 SDK 官方地址的 HTTPS 检查仍失败。因此本次不加入未经还原的通知依赖，也不把方案记录计为 Toast 已实现。内联编辑、XLSX、DPI／读屏等各自的前置条件仍见需求核对表。

## 上游依据

- [微软：在 .NET 应用中使用应用通知](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
- [微软：AppNotificationManager.Register](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.register?view=windows-app-sdk-2.0)
- [DesktopNotifications 原仓库](https://github.com/pr8x/DesktopNotifications)：核对时已标记于 2026-01-12 归档，不作为新的默认依赖。分支或 fork 需独立复核维护状态，不能沿用原包名推定可靠性。
