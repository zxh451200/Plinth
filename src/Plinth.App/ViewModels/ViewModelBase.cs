using CommunityToolkit.Mvvm.ComponentModel;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

/// <summary>界面语言切换后需要刷新自己生成的文字(下拉选项、状态行等)的页面。</summary>
public interface IUiLanguageAware
{
    void OnUiLanguageChanged();
}

public abstract class ViewModelBase : ObservableObject, IUiLanguageAware
{
    // Set only by the host after explicit optional page construction succeeds.
    internal OptionalModuleState? OptionalModule { get; set; }

    /// <summary>默认让所有绑定重读;需要重建选项集合的页面在覆盖里先重建再调用基类。</summary>
    public virtual void OnUiLanguageChanged() => OnPropertyChanged(string.Empty);
}
