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

    [Fact]
    public void IsValidListenerConfiguration_RejectsEachInvalidOperationalLimit()
    {
        var cases = new (string Name, Action<SpeedListenerConfiguration> MakeInvalid)[]
        {
            ("UDP port", value => value.UdpPort = 0),
            ("channel capacity", value => value.ChannelCapacity = 0),
            ("batch size", value => value.BatchSize = 0),
            ("batch larger than channel", value => value.BatchSize = value.ChannelCapacity + 1),
            ("flush interval", value => value.FlushInterval = TimeSpan.Zero),
            ("shutdown timeout", value => value.ShutdownFlushTimeout = TimeSpan.Zero),
            ("shutdown attempts", value => value.ShutdownMaxWriteAttempts = 0),
            ("shutdown attempts exceed normal attempts", value => value.ShutdownMaxWriteAttempts = value.MaxWriteAttempts + 1),
            ("mapping refresh", value => value.DeviceMappingRefreshInterval = TimeSpan.Zero),
            ("archive parallelism", value => value.ArchiveParallelism = 0),
            ("write timeout", value => value.WriteTimeout = TimeSpan.Zero),
            ("poison threshold", value => value.PoisonDeviceFailureThreshold = 0),
            ("summary interval", value => value.SummaryInterval = TimeSpan.Zero)
        };

        foreach (var testCase in cases)
        {
            var configuration = ValidConfiguration();
            testCase.MakeInvalid(configuration);
            Assert.False(HostBootstrapper.IsValidListenerConfiguration(configuration), testCase.Name);
        }
    }

    [Fact]
    public void IsValidListenerConfiguration_AcceptsValidBoundaryValues()
    {
        var configuration = ValidConfiguration();
        configuration.UdpPort = 65535;
        configuration.BatchSize = configuration.ChannelCapacity;
        configuration.ShutdownFlushTimeout = TimeSpan.FromTicks(
            configuration.WriteTimeout.Ticks * configuration.ShutdownMaxWriteAttempts + 1);

        Assert.True(HostBootstrapper.IsValidListenerConfiguration(configuration));
    }

    private static SpeedListenerConfiguration ValidConfiguration() => new()
    {
        UdpPort = 10088,
        ChannelCapacity = 100,
        BatchSize = 10,
        FlushInterval = TimeSpan.FromSeconds(1),
        ShutdownFlushTimeout = TimeSpan.FromSeconds(3),
        ShutdownMaxWriteAttempts = 2,
        MaxWriteAttempts = 3,
        DeviceMappingRefreshInterval = TimeSpan.FromMinutes(1),
        ArchiveParallelism = 1,
        WriteTimeout = TimeSpan.FromSeconds(1),
        PoisonDeviceFailureThreshold = 2,
        SummaryInterval = TimeSpan.FromMinutes(1)
    };
}
