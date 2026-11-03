using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class BrandingTests
{
    [Fact]
    public void Normalize_keeps_valid_values_and_https_support_url()
    {
        var options = new BrandingOptions
        {
            ProductName = "  My Admin  ",
            Description = "  Internal admin tool ",
            SupportUrl = "https://example.com/help",
            SupportText = "Help desk",
        }.Normalize();

        Assert.Equal("My Admin", options.ProductName);
        Assert.Equal("https://example.com/help", options.SupportUrl);
        Assert.Equal("Help desk", options.SupportText);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void Normalize_discards_non_https_support_urls(string url) =>
        Assert.Empty(new BrandingOptions { SupportUrl = url }.Normalize().SupportUrl);

    [Fact]
    public void Normalize_replaces_empty_or_oversized_values_with_defaults()
    {
        var options = new BrandingOptions
        {
            ProductName = "",
            Description = new string('x', 501),
            SupportText = new string('x', 201),
        }.Normalize();

        Assert.Equal(AppInfo.ProductName, options.ProductName);
        Assert.Empty(options.SupportText);
        Assert.NotEmpty(options.Description);
    }
}
