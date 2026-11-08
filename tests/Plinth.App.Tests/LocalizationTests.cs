using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>多语言文字的自动检查(规范 §9.3):防止漏翻译、key 写错、占位符对不上。</summary>
[Collection("Localization")]
public class LocalizationTests
{
    private static Dictionary<string, string> ReadResx(string fileName) =>
        XDocument.Load(Path.Combine(RepoPaths.Src, "Resources", fileName)).Root!
            .Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => (string)data.Element("value")!);

    [Fact]
    public void English_and_Chinese_have_the_same_keys()
    {
        var english = ReadResx("Strings.resx").Keys.ToHashSet();
        var chinese = ReadResx("Strings.zh-CN.resx").Keys.ToHashSet();

        Assert.Empty(english.Except(chinese));
        Assert.Empty(chinese.Except(english));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var english = ReadResx("Strings.resx");
        var chinese = ReadResx("Strings.zh-CN.resx");
        var placeholder = new Regex(@"\{\d+\}");

        foreach (var (key, englishText) in english)
        {
            var englishPlaceholders = placeholder.Matches(englishText).Select(m => m.Value).OrderBy(v => v);
            var chinesePlaceholders = placeholder.Matches(chinese[key]).Select(m => m.Value).OrderBy(v => v);
            Assert.True(englishPlaceholders.SequenceEqual(chinesePlaceholders), $"{key} 的 {{0}} 占位符中英文对不上");
        }
    }

    [Fact]
    public void Every_key_used_in_code_and_xaml_is_defined()
    {
        var defined = ReadResx("Strings.resx").Keys.ToHashSet();
        var used = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(RepoPaths.Src, "*.*", SearchOption.AllDirectories)
                     .Where(path => (path.EndsWith(".cs") || path.EndsWith(".axaml"))
                                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "Loc\\.(?:Get|Format)\\(\"([^\"]+)\""))
            {
                used.Add(match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(text, @"\{loc:T ([\w.]+)\}"))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.Empty(used.Except(defined));
    }

    [Theory]
    [InlineData("zh-CN", "nav.overview", "概览")]
    [InlineData("en-US", "nav.overview", "Overview")]
    [InlineData("zh-CN", "shell.status.tabCount", "3 个标签")]
    [InlineData("en-US", "shell.status.tabCount", "3 tab(s)")]
    public void Loc_returns_text_for_the_selected_language(string language, string key, string expected)
    {
        try
        {
            Loc.Initialize(language);

            var actual = key == "shell.status.tabCount" ? Loc.Format(key, 3) : Loc.Get(key);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage); // 恢复默认,别的测试断言的是中文
        }
    }

    [Fact]
    public void An_unknown_or_missing_language_falls_back_to_the_default()
    {
        try
        {
            Loc.Initialize("klingon");
            Assert.Equal("概览", Loc.Get("nav.overview"));

            Loc.Initialize(null);
            Assert.Equal("概览", Loc.Get("nav.overview"));
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage);
        }
    }

    [Fact]
    public void A_missing_key_shows_the_key_in_brackets()
    {
        Assert.Equal("[no.such.key]", Loc.Get("no.such.key"));
    }

    /// <summary>界面文字必须跟着启动语言走:英文界面不能混中文,中文界面不能混英文。</summary>
    [AvaloniaFact]
    public void Ui_strings_follow_the_selected_language()
    {
        try
        {
            Loc.Initialize("en-US");
            using (var shell = new Shell())
            {
                shell.Layout();
                Assert.Equal("Search pages (Ctrl+K)",
                    shell.Window.FindControl<TextBox>("QuickOpenBox")!.PlaceholderText);
                Assert.Equal("Environment: " + Loc.Get("shell.environment.development"),
                    shell.ViewModel.ShellStatus!.EnvironmentText);
                using var logs = new LogViewModel(new FolderService(NullLogger<FolderService>.Instance));
                Assert.Equal(["All levels", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal", "Critical"],
                    logs.LevelOptions);
            }

            Loc.Initialize("zh-CN");
            using (var shell = new Shell())
            {
                shell.Layout();
                Assert.Equal("搜索页面（Ctrl+K）",
                    shell.Window.FindControl<TextBox>("QuickOpenBox")!.PlaceholderText);
                Assert.Equal("环境：开发", shell.ViewModel.ShellStatus!.EnvironmentText);
                using var logs = new LogViewModel(new FolderService(NullLogger<FolderService>.Instance));
                Assert.Equal(["全部级别", "详细", "调试", "信息", "警告", "错误", "致命", "严重"],
                    logs.LevelOptions);
            }
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage);
        }
    }

    /// <summary>切换语言必须立即刷新已打开的界面,不需要重启。</summary>
    [AvaloniaFact]
    public void Switching_language_updates_the_open_window_immediately()
    {
        try
        {
            Loc.Initialize("zh-CN");
            using var shell = new Shell();
            shell.Layout();
            var quick = shell.Window.FindControl<TextBox>("QuickOpenBox")!;
            var overview = shell.ViewModel.PrimaryMenuItems.First(item => item.Id == ShellNavigation.OverviewId);
            var tab = shell.ViewModel.Tabs.First(item => item.Id == ShellNavigation.OverviewId);
            Assert.Equal("搜索页面（Ctrl+K）", quick.PlaceholderText);
            Assert.Equal("环境：开发", shell.ViewModel.ShellStatus!.EnvironmentText);
            Assert.Equal("概览", overview.Title);
            Assert.Equal("概览", tab.Title);

            Loc.SetLanguage("en-US");
            shell.Layout();

            Assert.Equal("Search pages (Ctrl+K)", quick.PlaceholderText);
            Assert.Equal("Environment: " + Loc.Get("shell.environment.development"),
                shell.ViewModel.ShellStatus!.EnvironmentText);
            Assert.Equal("Overview", overview.Title);
            Assert.Equal("Overview", tab.Title);
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage);
        }
    }
}
