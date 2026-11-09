using System;
using Plinth.App.Localization;

namespace Plinth.App.Services;

/// <summary>用户可见的安全摘要，不接受异常详情，不能把路径、凭据或请求正文带入弹窗。</summary>
public static class UnhandledErrorPresentation
{
    public const string ErrorCode = "UI_UNEXPECTED";
    public const string StartupErrorCode = "STARTUP_FAILED";

    public static TaskError Create() => new(ErrorCode,
        Loc.Get("error.body"), Loc.Get("tasks.error.next"), false, Guid.NewGuid().ToString("D"));

    // 启动失败时主窗口还不存在，不能走 ErrorDialog；原始异常只进同一关联 ID 的日志。
    public static TaskError CreateStartup() => new(StartupErrorCode,
        Loc.Get("startup.error.body"), Loc.Get("startup.error.next"), true, Guid.NewGuid().ToString("D"));

    // ErrorDialog 已在正文显示 error.body；此处只格式化建议与安全标识，避免正文重复。
    // 原异常保留在同一关联 ID 的日志中。
    public static string Format(TaskError error) =>
        $"{error.NextStep}{Environment.NewLine}"
        + $"{Loc.Get("logs.errorCode")}: {error.Code}{Environment.NewLine}"
        + $"{Loc.Get("logs.correlationId")}: {error.CorrelationId}";
}
