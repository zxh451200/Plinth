using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Plinth.App.Localization;

namespace Plinth.App.Navigation;

public enum PageCommandAction { New, Save, Delete, Cancel }

/// <summary>
/// 模块以普通 DI 集合贡献命令；解析器只接收当前已打开的页面，不创建新的页面实例。
/// Action 为空的命令显示在「数据 / 页面命令」中。
/// </summary>
public sealed record CommandMenuContribution(
    string PageId,
    string HeaderKey,
    Func<object, ICommand?> ResolveCommand,
    PageCommandAction? Action = null)
{
    public static void Validate(IEnumerable<CommandMenuContribution> contributions)
    {
        var duplicate = contributions.Where(item => item.Action.HasValue)
            .GroupBy(item => (item.PageId, item.Action))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Page '{duplicate.Key.PageId}' registers action '{duplicate.Key.Action}' more than once.");
    }
}

/// <summary>查找是视图焦点操作，由页面视图实现，不要求业务 VM 引用 Avalonia。</summary>
public interface IPageSearchTarget
{
    void FocusSearch();
}

/// <summary>转发当前页面的原命令及 CanExecute，不复制业务逻辑。</summary>
public sealed class CommandMenu : ICommand, IDisposable
{
    private readonly Func<ICommand?> _resolve;
    private ICommand? _target;

    public CommandMenu(string headerKey, Func<ICommand?> resolve)
    {
        Header = Loc.Get(headerKey);
        _resolve = resolve;
    }

    public string Header { get; }
    public ICommand Command => this;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _resolve()?.CanExecute(parameter) == true;

    public void Execute(object? parameter)
    {
        var command = _resolve();
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

    public void RefreshTarget()
    {
        var next = _resolve();
        if (!ReferenceEquals(next, _target))
        {
            if (_target is not null) _target.CanExecuteChanged -= OnCanExecuteChanged;
            _target = next;
            if (_target is not null) _target.CanExecuteChanged += OnCanExecuteChanged;
        }
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) =>
        CanExecuteChanged?.Invoke(this, e);

    public void Dispose()
    {
        if (_target is not null) _target.CanExecuteChanged -= OnCanExecuteChanged;
        _target = null;
    }
}
