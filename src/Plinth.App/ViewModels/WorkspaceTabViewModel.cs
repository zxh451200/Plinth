using CommunityToolkit.Mvvm.ComponentModel;

namespace Plinth.App.ViewModels;

/// <summary>一个打开的工作区标签。Content 由 ViewLocator 解析成对应 View。</summary>
public partial class WorkspaceTabViewModel : ViewModelBase
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
