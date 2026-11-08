using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Plinth.App.Modules.DockOptional;

namespace Plinth.DockDemo;

public sealed class DockDemoWindow : Window
{
    private readonly DockLayoutStore _store;
    private readonly Func<DemoDockFactory>? _factoryFactory;
    private readonly DockControl _dock = new() { InitializeLayout = false, InitializeFactory = false };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 6) };
    private readonly StackPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12),
    };
    private DockWorkspace? _workspace;
    private bool _busy;
    private bool _closed;
    private bool _closeApproved;

    public DockDemoWindow(string layoutPath, Func<DemoDockFactory>? factoryFactory = null)
    {
        _store = new DockLayoutStore(layoutPath);
        _factoryFactory = factoryFactory;
        Title = "Plinth · Dock 可选示例";
        Width = 1100;
        Height = 720;
        MinWidth = 640;
        MinHeight = 400;
        var save = new Button { Content = "保存布局" };
        var reset = new Button { Content = "重置布局" };
        var discard = new Button { Content = "不保存并退出" };
        save.Click += async (_, _) => await SaveAsync();
        reset.Click += (_, _) => Reset();
        discard.Click += (_, _) => { _closeApproved = true; Close(); };
        _toolbar.Children.Add(save);
        _toolbar.Children.Add(reset);
        _toolbar.Children.Add(discard);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(_toolbar);
        Grid.SetRow(_dock, 1);
        grid.Children.Add(_dock);
        Grid.SetRow(_status, 2);
        grid.Children.Add(_status);
        Content = grid;
        Opened += async (_, _) => await OpenAsync();
        Closing += async (_, args) =>
        {
            if (_closeApproved) return;
            args.Cancel = true;
            if (_busy) return;
            if (_workspace is null || await _workspace.TryLeaveBusinessPanesAsync() && await SaveAsync())
            {
                _closeApproved = true;
                Dispatcher.UIThread.Post(Close);
            }
        };
        Closed += (_, _) => { _closed = true; ReleaseWorkspace(); };
    }

    private async Task OpenAsync()
    {
        SetBusy(true);
        _status.Text = "正在恢复布局…";
        try
        {
            var workspace = await DockWorkspace.OpenAsync(_store, _factoryFactory?.Invoke() ?? new DemoDockFactory());
            if (_closed) { workspace.Dispose(); return; }
            Attach(workspace);
            _status.Text = workspace.RecoveryReason switch
            {
                null => "已恢复布局。可拖动页签和标题栏进行停靠、分割或浮动。",
                "missing" => "默认布局。退出时自动保存，也可点击“保存布局”。",
                _ => "保存的布局不可用，已恢复默认布局；原文件在下次保存前保留。",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReleaseWorkspace();
            _status.Text = "Dock 初始化失败。可点击“重置布局”重试或退出。";
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private async Task<bool> SaveAsync()
    {
        if (_busy || _workspace is null) return false;
        SetBusy(true);
        try
        {
            await _workspace.SaveAsync(_store);
            _status.Text = "布局已保存。";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "布局保存失败，窗口保持打开。可重试，或选择“不保存并退出”。";
            return false;
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private void Reset()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var next = DockWorkspace.CreateDefault(_factoryFactory?.Invoke() ?? new DemoDockFactory());
            try { ReleaseWorkspace(); }
            catch { next.Dispose(); throw; }
            Attach(next);
            _status.Text = "已重置布局；保存或退出后生效。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "重置失败，可重试或退出。";
        }
        finally { SetBusy(false); }
    }

    private void Attach(DockWorkspace workspace)
    {
        _workspace = workspace;
        _dock.Factory = workspace.Factory;
        _dock.Layout = workspace.Layout;
    }

    private void ReleaseWorkspace()
    {
        var workspace = _workspace;
        _workspace = null;
        try
        {
            // Layout=null invokes DockControl's real DeInitialize/cache cleanup.
            _dock.Layout = null;
            _dock.Factory = null;
        }
        finally { workspace?.Dispose(); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _toolbar.IsEnabled = !busy;
        _dock.IsDockingEnabled = !busy;
    }
}
