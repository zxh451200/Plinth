using System.Threading.Tasks;

namespace Plinth.App.Navigation;

/// <summary>页面离开保护。保存失败必须返回 false 并保留输入。</summary>
public interface IUnsavedChanges
{
    bool IsDirty { get; }
    Task<bool> TryLeaveAsync();
}

public enum UnsavedChangesChoice
{
    Cancel,
    Save,
    Discard,
}
