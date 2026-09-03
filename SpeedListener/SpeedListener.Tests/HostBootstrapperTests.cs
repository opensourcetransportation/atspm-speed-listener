using SpeedListener.Configuration;

namespace SpeedListener.Tests;

public sealed class HostBootstrapperTests
{
    [Fact]
    public void IsValidListenerConfiguration_RequiresShutdownBudgetForEveryAttempt()
    {
        var configuration = new SpeedListenerConfiguration
        {
            WriteTimeout = TimeSpan.FromSeconds(10),
            ShutdownMaxWriteAttempts = 2,
            MaxWriteAttempts = 3,
            ShutdownFlushTimeout = TimeSpan.FromSeconds(15)
        };

        Assert.False(HostBootstrapper.IsValidListenerConfiguration(configuration));

        configuration.ShutdownFlushTimeout = TimeSpan.FromSeconds(21);
        Assert.True(HostBootstrapper.IsValidListenerConfiguration(configuration));
    }
}
