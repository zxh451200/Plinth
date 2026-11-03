using System;
using System.IO;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class InstallationGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-install-" + Guid.NewGuid().ToString("N"));
    private string LockPath => Path.Combine(_directory, InstallationGuard.FileName);

    public InstallationGuardTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(LockPath, "shipped marker");
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Missing_marker_fails_without_creating_installation_files()
    {
        File.Delete(LockPath);
        Assert.Throws<FileNotFoundException>(() => InstallationGuard.Acquire(_directory));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Application_opens_read_only_and_multiple_data_instances_can_share()
    {
        using var first = InstallationGuard.Acquire(_directory);
        using var second = InstallationGuard.Acquire(_directory);
        Assert.True(first.CanRead);
        Assert.False(first.CanWrite);
        Assert.Equal("shipped marker", File.ReadAllText(LockPath));
    }

    [Fact]
    public void Windows_installer_waits_for_every_application_reader()
    {
        // FILE_SHARE_DELETE is the NSIS handle's sharing mode. This assertion is Windows-specific.
        if (!OperatingSystem.IsWindows()) return;
        var first = InstallationGuard.Acquire(_directory);
        var second = InstallationGuard.Acquire(_directory);
        try
        {
            Assert.Throws<IOException>(() => OpenInstaller());
            first.Dispose();
            Assert.Throws<IOException>(() => OpenInstaller());
            second.Dispose();
            using var installer = OpenInstaller();
            Assert.Throws<IOException>(() => InstallationGuard.Acquire(_directory));
            Assert.Throws<IOException>(() => OpenInstaller());
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
        using var resumed = InstallationGuard.Acquire(_directory);
    }

    private FileStream OpenInstaller() =>
        new(LockPath, FileMode.Open, FileAccess.Read, FileShare.Delete);
}
