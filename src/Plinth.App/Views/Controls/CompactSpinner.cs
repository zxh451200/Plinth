using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Plinth.App.Views.Controls;

/// <summary>
/// Fluent 的 ButtonSpinner 模板把上下按钮写死成 34px 宽,窄数字框会被按钮占掉大半。
/// 首次布局出现按钮后收窄到 22px,随后解除监听。
/// </summary>
public static class CompactSpinner
{
    public const double ButtonWidth = 22;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TemplatedControl, bool>("IsEnabled", typeof(CompactSpinner));

    static CompactSpinner()
    {
        IsEnabledProperty.Changed.AddClassHandler<TemplatedControl>((control, args) =>
        {
            if (args.NewValue is not true || TryPatch(control)) return;
            control.LayoutUpdated += OnLayoutUpdated;
            // 属性可能在一次布局中途设置,当前轮不会再有 LayoutUpdated；下一轮再试一次。
            Dispatcher.UIThread.Post(() => OnLayoutUpdated(control, EventArgs.Empty), DispatcherPriority.Loaded);
        });
    }

    public static bool GetIsEnabled(TemplatedControl control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TemplatedControl control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is TemplatedControl control && TryPatch(control))
            control.LayoutUpdated -= OnLayoutUpdated;
    }

    private static bool TryPatch(TemplatedControl control)
    {
        var buttons = control.GetVisualDescendants().OfType<RepeatButton>()
            .Where(button => button.Name is "PART_IncreaseButton" or "PART_DecreaseButton")
            .ToArray();
        if (buttons.Length == 0) return false;
        foreach (var button in buttons)
        {
            button.MinWidth = ButtonWidth;
            button.Width = ButtonWidth;
            foreach (var icon in button.GetVisualDescendants().OfType<PathIcon>())
            {
                icon.Width = 12;
                icon.Height = 6;
            }
        }
        return true;
    }
}
