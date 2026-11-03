namespace Plinth.App.Navigation;

/// <summary>模块通过普通 DI 集合提供离线帮助，不需要修改 Shell。</summary>
public sealed record HelpTopic(string Key, string Title, string Body,
    string? PageId = null, string[]? ErrorCodes = null);
