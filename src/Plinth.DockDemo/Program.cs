using Avalonia;

namespace Plinth.DockDemo;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<DockDemoApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
