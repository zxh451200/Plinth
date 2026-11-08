using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Plinth.App.Navigation;

public enum ShortcutAction { New, Save, Find, Delete, Cancel, CloseTab, Refresh, Help, ToggleSidebar }

public sealed record AppShortcut(ShortcutAction Action, KeyGesture Gesture, bool AllowInTextInput = true);

/// <summary>首版固定快捷键：菜单显示和窗口分发共用，不提供用户自定义编辑器。</summary>
public static class AppShortcuts
{
    public static KeyGesture New { get; } = new(Key.N, KeyModifiers.Control);
    public static KeyGesture Save { get; } = new(Key.S, KeyModifiers.Control);
    public static KeyGesture Find { get; } = new(Key.F, KeyModifiers.Control);
    public static KeyGesture Delete { get; } = new(Key.Delete);
    public static KeyGesture Cancel { get; } = new(Key.Escape);
    public static KeyGesture CloseTab { get; } = new(Key.W, KeyModifiers.Control);
    public static KeyGesture Refresh { get; } = new(Key.F5);
    public static KeyGesture Help { get; } = new(Key.F1);
    public static KeyGesture Sidebar { get; } = new(Key.B, KeyModifiers.Control);

    public static IReadOnlyList<AppShortcut> All { get; } = Array.AsReadOnly<AppShortcut>(
    [
        new(ShortcutAction.New, New),
        new(ShortcutAction.Save, Save),
        new(ShortcutAction.Find, Find),
        new(ShortcutAction.Delete, Delete, AllowInTextInput: false),
        new(ShortcutAction.Cancel, Cancel, AllowInTextInput: false),
        new(ShortcutAction.CloseTab, CloseTab),
        new(ShortcutAction.Refresh, Refresh),
        new(ShortcutAction.Help, Help),
        new(ShortcutAction.ToggleSidebar, Sidebar),
    ]);

    public static ShortcutAction? Match(KeyEventArgs e, bool isTextInput)
    {
        if (e.Handled) return null;
        foreach (var shortcut in All)
            if ((!isTextInput || shortcut.AllowInTextInput) && shortcut.Gesture.Matches(e))
                return shortcut.Action;
        return null;
    }

    public static void Validate(IEnumerable<AppShortcut> shortcuts)
    {
        var gestures = new Dictionary<(Key, KeyModifiers), ShortcutAction>();
        var actions = new HashSet<ShortcutAction>();
        foreach (var shortcut in shortcuts)
        {
            var gesture = shortcut.Gesture;
            if (gesture.KeyModifiers == KeyModifiers.Control
                && gesture.Key is Key.C or Key.V or Key.X or Key.A or Key.Z or Key.Y)
                throw new InvalidOperationException($"Shortcut '{gesture}' is reserved for text editing.");
            // 与 Avalonia KeyGesture.Matches 的等价键规则保持一致。
            var normalizedKey = gesture.Key switch
            {
                Key.Add => Key.OemPlus,
                Key.Subtract => Key.OemMinus,
                Key.Decimal => Key.OemPeriod,
                _ => gesture.Key,
            };
            var key = (normalizedKey, gesture.KeyModifiers);
            if (gestures.TryGetValue(key, out var existing))
                throw new InvalidOperationException(
                    $"Shortcut '{gesture}' conflicts between '{existing}' and '{shortcut.Action}'.");
            gestures.Add(key, shortcut.Action);
            if (!actions.Add(shortcut.Action))
                throw new InvalidOperationException($"Shortcut action '{shortcut.Action}' is registered more than once.");
        }
    }
}
