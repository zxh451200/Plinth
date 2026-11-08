namespace Plinth.App.Services;

/// <summary>下拉选项的最小契约:稳定键 + 显示名。可搜索下拉按 Title 过滤。</summary>
public interface ISelectOption
{
    string Key { get; }

    string Title { get; }
}
