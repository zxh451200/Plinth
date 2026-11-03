using System.Text.Json;
using Avalonia;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>窗口位置记忆:保存的位置只有在屏幕上够得着时才恢复。</summary>
public sealed class WindowPlacementTests
{
    private static readonly PixelRect[] OneScreen = [new PixelRect(0, 0, 1920, 1040)];

    private static WindowPlacement At(int x, int y, double width = 1200, double height = 720) =>
        new() { X = x, Y = y, Width = width, Height = height };

    [Fact]
    public void A_placement_inside_the_screen_is_usable() =>
        Assert.True(WindowPlacementStore.IsUsable(At(100, 80), OneScreen));

    [Fact]
    public void A_placement_that_is_off_every_screen_is_not_usable()
    {
        // 例如上次在第二块显示器上,这次只剩一块
        Assert.False(WindowPlacementStore.IsUsable(At(2500, 100), OneScreen));
        Assert.False(WindowPlacementStore.IsUsable(At(100, -500), OneScreen));
    }

    [Fact]
    public void A_placement_on_the_second_screen_is_usable_when_that_screen_exists()
    {
        PixelRect[] two = [new PixelRect(0, 0, 1920, 1040), new PixelRect(1920, 0, 1920, 1040)];

        Assert.True(WindowPlacementStore.IsUsable(At(2500, 100), two));
    }

    [Fact]
    public void Missing_or_tiny_sizes_are_not_usable()
    {
        Assert.False(WindowPlacementStore.IsUsable(null, OneScreen));
        Assert.False(WindowPlacementStore.IsUsable(At(100, 80, width: 0), OneScreen));
        Assert.False(WindowPlacementStore.IsUsable(At(100, 80, height: 50), OneScreen));
    }

    [Fact]
    public void The_placement_survives_a_json_round_trip()
    {
        var settings = new AppSettings { Window = At(10, 20, 1000, 600) };
        settings.Window.Maximized = true;

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(back?.Window);
        Assert.Equal(10, back.Window.X);
        Assert.Equal(600, back.Window.Height);
        Assert.True(back.Window.Maximized);
    }
}
