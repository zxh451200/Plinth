using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Serilog;

namespace Plinth.App;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        if (param is ViewModelBase { OptionalModule: { } module })
        {
            if (!module.Failed)
            {
                try
                {
                    var viewType = FindViewType(param.GetType());
                    if (viewType is null || !typeof(Control).IsAssignableFrom(viewType))
                        throw new InvalidOperationException();
                    return (Control)(Activator.CreateInstance(viewType)
                        ?? throw new InvalidOperationException());
                }
                catch (Exception ex) when (OptionalModuleState.IsRecoverable(ex))
                {
                    module.FailViewConstruction();
                }
            }
            // No exception, type name or deployment path is retained in the fallback.
            return new TextBlock
            {
                Text = Loc.Format("shell.module.failed", module.ModuleName, module.CorrelationId),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(24),
            };
        }

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = FindViewType(param.GetType());

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        // 找不到时不能只静默显示一行字:写一条警告日志,并在页面上给出能照着改的提示
        Log.Warning("找不到页面 {ViewModel} 对应的 View:{ExpectedView}", param.GetType().FullName, name);
        return new TextBlock
        {
            Text = $"找不到页面「{param.GetType().Name}」对应的界面。\n"
                 + $"需要一个叫 {name} 的 View(名字把 ViewModel 换成 View,命名空间对应)。\n"
                 + "请检查 View 的类名、命名空间和 x:Class 是否拼写一致。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(24),
        };
    }

    private static Type? FindViewType(Type viewModelType)
    {
        var name = viewModelType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        // Module views live beside their VMs; retain the original host lookup as fallback.
        return viewModelType.Assembly.GetType(name) ?? Type.GetType(name);
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
