namespace Plinth.App.Navigation;

/// <summary>页面支持 F5 刷新时实现;Shell 只对当前标签调用。</summary>
public interface IRefreshable
{
    void Refresh();
}
