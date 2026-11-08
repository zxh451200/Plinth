namespace Plinth.App;

/// <summary>第二次启动或系统通知点击要求已有实例做什么。只允许固定的受控入口。</summary>
public enum ActivationIntent
{
    Show,
    TaskCenter,
}
