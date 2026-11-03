using Plinth.App.Modules.Demo.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CsvLiteralRoundTripTests
{
    [Theory]
    [InlineData("'=1+2")]
    [InlineData("'plain")]
    [InlineData("''=1+2")]
    [InlineData("=1+2")]
    [InlineData("  @SUM(A1)")]
    public void Formula_protection_preserves_literal_apostrophes(string value)
    {
        var csv = CsvWriter.Build(["value"], [new[] { value }]);
        var rows = CsvReader.Parse(csv);
        Assert.Equal(value, rows[1][0]);
    }
}
