using Plinth.App;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>系统通知点击只认一个固定 URI;其它参数仍按原规则拒绝,不扩大命令面。</summary>
public sealed class StartupArgumentsTests
{
    [Theory]
    [InlineData("plinth://tasks")]
    [InlineData("PLINTH://TASKS")]
    public void Task_center_uri_routes_to_the_controlled_entry(string argument)
    {
        var (intent, args) = StartupArguments.Parse([argument]);

        Assert.Equal(ActivationIntent.TaskCenter, intent);
        Assert.Empty(args);
    }

    [Fact]
    public void Ordinary_arguments_are_untouched()
    {
        var (intent, args) = StartupArguments.Parse(["--version"]);

        Assert.Equal(ActivationIntent.Show, intent);
        Assert.Equal(new[] { "--version" }, args);
    }

    [Fact]
    public void Uri_with_extra_arguments_is_not_treated_as_a_notification_entry()
    {
        var (intent, args) = StartupArguments.Parse(["plinth://tasks", "--unknown"]);

        Assert.Equal(ActivationIntent.Show, intent);
        Assert.Equal(2, args.Length);
    }
}
