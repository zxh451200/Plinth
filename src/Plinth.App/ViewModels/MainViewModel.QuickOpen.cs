using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Navigation;

namespace Plinth.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<NavigationItem> QuickOpenItems { get; } = [];

    [ObservableProperty]
    public partial bool IsQuickOpen { get; set; }

    [ObservableProperty]
    public partial string QuickOpenText { get; set; } = "";

    [ObservableProperty]
    public partial NavigationItem? QuickOpenSelection { get; set; }

    partial void OnQuickOpenTextChanged(string value) => RefreshQuickOpen();

    [RelayCommand]
    private void OpenQuickOpen()
    {
        if (_released) return;
        QuickOpenText = "";
        RefreshQuickOpen();
        QuickOpenSelection = QuickOpenItems.FirstOrDefault();
        IsQuickOpen = true;
    }

    [RelayCommand]
    private void CloseQuickOpen() => IsQuickOpen = false;

    [RelayCommand]
    private void AcceptQuickOpen(NavigationItem? item = null)
    {
        var target = item ?? QuickOpenSelection;
        if (target is not null) OpenTab(target);
        IsQuickOpen = false;
    }

    private void RefreshQuickOpen()
    {
        var query = QuickOpenText.Trim();
        var matches = MenuItems.Where(item =>
            query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        QuickOpenItems.Clear();
        foreach (var item in matches) QuickOpenItems.Add(item);
        if (QuickOpenSelection is null || !QuickOpenItems.Contains(QuickOpenSelection))
            QuickOpenSelection = QuickOpenItems.FirstOrDefault();
    }
}
