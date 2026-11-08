using Plinth.App.Localization;

namespace Plinth.App.Navigation;

/// <summary>模块通过普通 DI 集合提供离线帮助，不需要修改 Shell。</summary>
public sealed record HelpTopic(string Key, string Title, string Body,
    string? PageId = null, string[]? ErrorCodes = null)
{
    /// <summary>给出 key 时按当前界面语言取文字,切换语言后立即生效;否则用传入的固定文字。</summary>
    public string? TitleKey { get; init; }

    public string? BodyKey { get; init; }

    public string DisplayTitle => TitleKey is null ? Title : Loc.Get(TitleKey);

    public string DisplayBody => BodyKey is null ? Body : Loc.Get(BodyKey);
}
