using System.Text;
using Avalonia.Headless.XUnit;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class LicenseNoticeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "plinth-license-" + Guid.NewGuid().ToString("N"));

    public LicenseNoticeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private string NoticePath => Path.Combine(_directory, LicenseNoticeService.FileName);

    [Fact]
    public async Task Missing_file_and_directory_return_empty_safe_state()
    {
        var result = await new LicenseNoticeService(_directory).ReadAsync();
        Assert.Equal(LicenseNoticeStatus.Missing, result.Status);
        Assert.Empty(result.Text);
        result = await new LicenseNoticeService(Path.Combine(_directory, "missing")).ReadAsync();
        Assert.Equal(LicenseNoticeStatus.Missing, result.Status);
        Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf8_is_read_literally_with_or_without_bom(bool bom)
    {
        const string text = "# 测试许可\n[link](https://example.invalid)\n<script>alert('test')</script>\n";
        await File.WriteAllTextAsync(NoticePath, text, new UTF8Encoding(bom));
        var result = await new LicenseNoticeService(_directory).ReadAsync();
        Assert.Equal(LicenseNoticeStatus.Loaded, result.Status);
        Assert.Equal(text, result.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Byte_limit_accepts_exact_limit_and_rejects_one_extra_byte(int extra)
    {
        await File.WriteAllTextAsync(NoticePath,
            new string('a', LicenseNoticeService.MaximumBytes + extra), new UTF8Encoding(false));
        var result = await new LicenseNoticeService(_directory).ReadAsync();
        Assert.Equal(extra == 0 ? LicenseNoticeStatus.Loaded : LicenseNoticeStatus.TooLarge, result.Status);
        Assert.Equal(extra == 0 ? LicenseNoticeService.MaximumBytes : 0, result.Text.Length);
    }

    [Fact]
    public async Task Invalid_utf8_is_unreadable_without_partial_content()
    {
        await File.WriteAllBytesAsync(NoticePath, [0x61, 0xc3, 0x28]);
        var result = await new LicenseNoticeService(_directory).ReadAsync();
        Assert.Equal(LicenseNoticeStatus.Unreadable, result.Status);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task Directory_in_place_of_file_is_unreadable()
    {
        Directory.CreateDirectory(NoticePath);
        var result = await new LicenseNoticeService(_directory).ReadAsync();
        Assert.Equal(LicenseNoticeStatus.Unreadable, result.Status);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task Cancellation_is_propagated_for_a_real_file()
    {
        await File.WriteAllTextAsync(NoticePath, "synthetic notice");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new LicenseNoticeService(_directory).ReadAsync(cancellation.Token));
    }

    [AvaloniaFact]
    public async Task View_model_coalesces_busy_loads_and_clears_stale_text_on_failure()
    {
        await File.WriteAllTextAsync(NoticePath, "synthetic notice");
        var vm = new LicenseViewModel(new LicenseNoticeService(_directory));
        var first = vm.LoadAsync();
        Assert.True(vm.IsBusy);
        Assert.Same(first, vm.LoadAsync());
        await first;
        Assert.False(vm.IsBusy);
        Assert.Equal("synthetic notice", vm.Text);
        File.Delete(NoticePath);
        await vm.LoadAsync();
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Text);
    }

    [AvaloniaFact]
    public async Task Detach_cancels_and_immediate_reattach_can_load_again()
    {
        await File.WriteAllTextAsync(NoticePath, "synthetic notice");
        var vm = new LicenseViewModel(new LicenseNoticeService(_directory));
        var first = vm.LoadAsync();
        vm.PageClosed();
        var second = vm.LoadAsync();
        await Task.WhenAll(first, second);
        Assert.False(vm.IsBusy);
        Assert.Equal("synthetic notice", vm.Text);
    }
}
