namespace SpeedListener.Tests;

public sealed class StartupDiagnosticLimitTests
{
    [Theory]
    [InlineData("bad", 0)]
    [InlineData("-1", 0)]
    [InlineData("999999999999999999999999999", 0)]
    [InlineData("0", 0)]
    [InlineData("1024", 1024)]
    [InlineData(null, 10485760)]
    [InlineData("", 10485760)]
    public void InvalidLimitFallsBackToUnlimited_AndValidLimitsArePreserved(string? value, long expected)
    {
        Assert.Equal(expected, Program.ParseDiagnosticLimit(value));
    }
}
