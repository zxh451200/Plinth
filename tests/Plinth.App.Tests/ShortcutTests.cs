using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class ShortcutTests
{
    [Fact]
    public void Defaults_are_unique_and_matching_requires_the_exact_modifiers()
    {
        AppShortcuts.Validate(AppShortcuts.All);
        Assert.Equal(9, AppShortcuts.All.Count);
        Assert.Equal(ShortcutAction.ToggleSidebar, AppShortcuts.Match(
            new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Control }, isTextInput: true));
        Assert.Equal(ShortcutAction.Save, AppShortcuts.Match(
            new KeyEventArgs { Key = Key.S, KeyModifiers = KeyModifiers.Control }, isTextInput: true));
        Assert.Null(AppShortcuts.Match(new KeyEventArgs
        {
            Key = Key.S,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        }, isTextInput: true));
    }

    [Fact]
    public void Conflicts_identify_both_actions_and_duplicate_actions_are_rejected()
    {
        var duplicateGesture = Assert.Throws<InvalidOperationException>(() => AppShortcuts.Validate(
        [
            new(ShortcutAction.Save, KeyGesture.Parse("Ctrl+S")),
            new(ShortcutAction.Help, KeyGesture.Parse("Control+S")),
        ]));
        Assert.Contains("Save", duplicateGesture.Message);
        Assert.Contains("Help", duplicateGesture.Message);
        Assert.Throws<InvalidOperationException>(() => AppShortcuts.Validate(
        [
            new(ShortcutAction.Save, AppShortcuts.Save),
            new(ShortcutAction.Save, AppShortcuts.Help),
        ]));
    }

    [Theory]
    [InlineData(Key.Add, Key.OemPlus)]
    [InlineData(Key.Subtract, Key.OemMinus)]
    [InlineData(Key.Decimal, Key.OemPeriod)]
    public void Equivalent_keypad_gestures_are_detected_as_conflicts(Key first, Key second)
    {
        Assert.Throws<InvalidOperationException>(() => AppShortcuts.Validate(
        [
            new(ShortcutAction.New, new KeyGesture(first, KeyModifiers.Control)),
            new(ShortcutAction.Save, new KeyGesture(second, KeyModifiers.Control)),
        ]));
    }

    [Theory]
    [InlineData(Key.C)]
    [InlineData(Key.V)]
    [InlineData(Key.X)]
    [InlineData(Key.A)]
    [InlineData(Key.Z)]
    [InlineData(Key.Y)]
    public void Standard_text_editing_gestures_cannot_become_application_shortcuts(Key key)
    {
        Assert.Throws<InvalidOperationException>(() => AppShortcuts.Validate(
            [new(ShortcutAction.New, new KeyGesture(key, KeyModifiers.Control))]));
    }

    [Theory]
    [InlineData(Key.Delete, ShortcutAction.Delete)]
    [InlineData(Key.Escape, ShortcutAction.Cancel)]
    public void Text_inputs_and_already_handled_events_keep_priority(Key key, ShortcutAction action)
    {
        var e = new KeyEventArgs { Key = key };
        Assert.Null(AppShortcuts.Match(e, isTextInput: true));
        Assert.Equal(action, AppShortcuts.Match(e, isTextInput: false));
        e.Handled = true;
        Assert.Null(AppShortcuts.Match(e, isTextInput: false));
    }

    [Fact]
    public void Page_actions_allow_different_pages_but_reject_a_duplicate_on_one_page()
    {
        var command = new RelayCommand(() => { });
        CommandMenuContribution[] valid =
        [
            new("one", "menu.file.save", _ => command, PageCommandAction.Save),
            new("two", "menu.file.save", _ => command, PageCommandAction.Save),
            new("one", "demo.export", _ => command),
            new("one", "notes.reload", _ => command),
        ];
        CommandMenuContribution.Validate(valid);
        var error = Assert.Throws<InvalidOperationException>(() =>
            CommandMenuContribution.Validate(valid.Append(
                new("one", "demo.save", _ => command, PageCommandAction.Save))));
        Assert.Contains("one", error.Message);
        Assert.Contains("Save", error.Message);
    }

    [AvaloniaFact]
    public void Menus_display_the_gestures_used_for_window_dispatch()
    {
        using var shell = new Shell();
        var items = shell.Window.GetLogicalDescendants().OfType<MenuItem>().ToArray();
        var save = Assert.Single(items.Where(item => ReferenceEquals(item.Command, shell.ViewModel.SavePageCommand)));
        Assert.Same(AppShortcuts.Save, save.InputGesture);
        var find = shell.Window.FindControl<MenuItem>("FindPageMenu")!;
        Assert.Same(AppShortcuts.Find, find.InputGesture);
        Assert.All(items.Where(item => ReferenceEquals(item.Command, shell.ViewModel.CloseTabCommand)),
            item => Assert.Same(AppShortcuts.CloseTab, item.InputGesture));
        Assert.Empty(shell.Window.KeyBindings);
    }

    [AvaloniaFact]
    public void Refresh_runs_once_and_close_still_uses_the_active_tab()
    {
        using var shell = new Shell();
        var page = new RefreshPage();
        var tab = new WorkspaceTabViewModel("test.shortcuts", "Test", page);
        shell.ViewModel.Tabs.Add(tab);
        shell.ViewModel.SelectedTab = tab;
        shell.Layout();
        shell.TabHost.Focusable = true;
        Assert.True(shell.TabHost.Focus());
        shell.Press(Key.F5, PhysicalKey.F5);
        Assert.Equal(1, page.Refreshes);
        shell.Press(Key.W, PhysicalKey.W, RawInputModifiers.Control);
        Assert.DoesNotContain(tab, shell.ViewModel.Tabs);
    }

    private sealed class RefreshPage : ViewModelBase, IRefreshable
    {
        public int Refreshes { get; private set; }
        public void Refresh() => Refreshes++;
    }
}
