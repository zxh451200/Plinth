using CommunityToolkit.Mvvm.ComponentModel;

namespace Plinth.App.ViewModels;

/// <summary>一个打开的工作区标签。Content 由 ViewLocator 解析成对应 View。</summary>
/// <remarks>
/// 标签本身不是页面:继承 <see cref="ObservableObject"/> 而不是 ViewModelBase,
/// 否则 ViewLocator 会把标签当页面去找不存在的 WorkspaceTabView,并回退成「找不到页面」。
/// </remarks>
public partial class WorkspaceTabViewModel : ObservableObject
{
    public WorkspaceTabViewModel(string id, string title, ViewModelBase content)
    {
        Id = id;
        Title = title;
        Content = content;
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    public ViewModelBase Content { get; }
}
