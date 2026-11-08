using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Plinth.App.Navigation;

namespace Plinth.App.ViewModels;

public partial class HelpViewModel : ViewModelBase
{
    private readonly HelpTopic[] _topics;

    public HelpViewModel(IEnumerable<HelpTopic> topics)
    {
        _topics = topics.ToArray();
        if (_topics.Any(topic => string.IsNullOrWhiteSpace(topic.Key))
            || _topics.Select(topic => topic.Key).Distinct(StringComparer.Ordinal).Count() != _topics.Length)
            throw new InvalidOperationException("Help topic keys must be nonempty and unique.");
        FilterTopics();
    }

    public string VersionText => $"v{AppInfo.Version}";
    public ObservableCollection<HelpTopic> Topics { get; } = [];
    public bool HasTopics => Topics.Count > 0;
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";
    [ObservableProperty]
    public partial HelpTopic? SelectedTopic { get; set; }

    partial void OnSearchTextChanged(string value) => FilterTopics();

    private void FilterTopics()
    {
        var selected = SelectedTopic;
        var query = SearchText.Trim();
        Topics.Clear();
        foreach (var topic in _topics.Where(topic => query.Length == 0
            || topic.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
            || topic.DisplayBody.Contains(query, StringComparison.OrdinalIgnoreCase)
            || topic.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || topic.ErrorCodes?.Any(code => code.Contains(query, StringComparison.OrdinalIgnoreCase)) == true))
            Topics.Add(topic);
        SelectedTopic = selected is not null && Topics.Contains(selected) ? selected : Topics.FirstOrDefault();
        OnPropertyChanged(nameof(HasTopics));
    }

    public override void OnUiLanguageChanged()
    {
        var selected = SelectedTopic?.Key;
        FilterTopics();
        if (selected is not null)
            SelectedTopic = Topics.FirstOrDefault(topic => topic.Key == selected) ?? SelectedTopic;
        base.OnUiLanguageChanged();
    }

    public void ShowTopic(string? key)
    {
        SearchText = "";
        SelectedTopic = _topics.FirstOrDefault(topic => topic.Key == key
            || key is not null && topic.ErrorCodes?.Contains(key, StringComparer.Ordinal) == true)
            ?? _topics.FirstOrDefault(topic => topic.Key == "start") ?? _topics.FirstOrDefault();
    }

    public void ShowForPage(string? pageId)
    {
        if (pageId == ShellNavigation.HelpId) return;
        ShowTopic(pageId is null ? null : _topics.FirstOrDefault(topic => topic.PageId == pageId)?.Key);
    }
}
