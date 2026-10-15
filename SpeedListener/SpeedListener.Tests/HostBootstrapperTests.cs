using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            ("database writer parallelism", value => value.DatabaseWriteParallelism = 0),
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
            configuration.WriteTimeout.Ticks * configuration.ShutdownMaxWriteAttempts + TimeSpan.FromMilliseconds(300).Ticks + 1);

        Assert.True(HostBootstrapper.IsValidListenerConfiguration(configuration));
    }

    [Fact]
    public void IsValidListenerConfiguration_RejectsBudgetWithoutRetryDelayAndInvalidZone()
    {
        var configuration = ValidConfiguration();
        configuration.ShutdownFlushTimeout = TimeSpan.FromSeconds(2.2);
        Assert.False(HostBootstrapper.IsValidListenerConfiguration(configuration));
        configuration.ShutdownFlushTimeout = TimeSpan.FromSeconds(3);
        configuration.EventTimeZoneId = "not-a-time-zone";
        Assert.False(HostBootstrapper.IsValidListenerConfiguration(configuration));
    }

    [Theory]
    [InlineData("sensor", 2103, "502620", false)]
    [InlineData("10.0.0.1", 0, "502620", false)]
    [InlineData("10.0.0.1", 2103, "5026AA", false)]
    [InlineData("10.0.0.1", 2103, "50262", false)]
    [InlineData("10.0.0.1", 2103, "502620", true)]
    [InlineData("::1", 2103, "502620", true)]
    public void IsValidListenerConfiguration_ValidatesUntaggedEndpointMapping(string address, int port, string detector, bool valid)
    {
        var config = ValidConfiguration();
        config.UntaggedSpeedDetectorMappings.Add(new() { SourceAddress = address, SourcePort = port, DetectorId = detector });
        Assert.Equal(valid, HostBootstrapper.IsValidListenerConfiguration(config));
    }

    [Fact]
    public void JsonBinding_UntaggedEndpointListProducesWorkingParser_AndRejectsDuplicateEndpoints()
    {
        var json = """
            { "SpeedListenerConfiguration": { "UntaggedSpeedDetectorMappings": [
              { "SourceAddress": "127.0.0.1", "SourcePort": 2103, "DetectorId": "527148" }
            ] } }
            """;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonStream(input).Build();
        var config = configuration.GetSection("SpeedListenerConfiguration").Get<SpeedListenerConfiguration>()!;
        Assert.True(HostBootstrapper.IsValidListenerConfiguration(config));
        var parser = new SpeedListener.Parsing.SpeedPacketParser(Microsoft.Extensions.Options.Options.Create(config));
        var result = parser.Parse(new SpeedListener.Receivers.UdpDatagram(Convert.FromHexString("585332507E0D0D"),
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2103), DateTimeOffset.UtcNow));
        Assert.Equal("527148", result.Event!.DetectorId);
        config.UntaggedSpeedDetectorMappings.Add(new() { SourceAddress = "127.0.0.1", SourcePort = 2103, DetectorId = "527132" });
        Assert.False(HostBootstrapper.IsValidListenerConfiguration(config));
    }

    [Fact]
    public void JsonBinding_LegacyColonDictionaryCannotSilentlyProduceEmptyValidMapping()
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{ "SpeedListenerConfiguration": { "UntaggedSpeedDetectorMappings": { "192.0.2.10:2102": "502620" } } }"""));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonStream(input).Build();
        var config = configuration.GetSection("SpeedListenerConfiguration").Get<SpeedListenerConfiguration>()!;
        Assert.False(HostBootstrapper.IsValidListenerConfiguration(config));
    }

    [Fact]
    public void GeneratorHostSetsDatabaseTimestampCompatibilityBeforeBuildingServices()
    {
        using var host = HostBootstrapper.BuildGeneratorHost();
        Assert.True(AppContext.TryGetSwitch("Npgsql.EnableLegacyTimestampBehavior", out var enabled) && enabled);
    }

    [Fact]
    public void GeneratorBuildValidatesOptionsEvenWhenHostIsNeverStarted()
    {
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            HostBootstrapper.BuildGeneratorHost(config => config.EventTimeZoneId = "invalid-zone"));
    }

    [Theory]
    [InlineData("127.1", false)]
    [InlineData("2130706433", false)]
    [InlineData("0x7f000001", false)]
    [InlineData("010.0.0.1", false)]
    [InlineData("192.168.001.1", false)]
    [InlineData("::ffff:192.0.2.010", false)]
    [InlineData("127.0.0.1.", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.0.2.10", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("::ffff:192.0.2.10", true)]
    public void SourceAddressesRequireCanonicalDottedQuadOrIpv6(string address, bool valid)
    {
        var config = ValidConfiguration();
        config.UntaggedSpeedDetectorMappings.Add(new() { SourceAddress = address, SourcePort = 2103, DetectorId = "527148" });
        Assert.Equal(valid, HostBootstrapper.IsValidListenerConfiguration(config));
    }

    [Fact]
    public void MappedIpv4AndPlainIpv4CannotConfigureDifferentDetectorsForSameSource()
    {
        var config = ValidConfiguration();
        config.UntaggedSpeedDetectorMappings.Add(new() { SourceAddress = "127.0.0.1", SourcePort = 2103, DetectorId = "502620" });
        config.UntaggedSpeedDetectorMappings.Add(new() { SourceAddress = "::ffff:127.0.0.1", SourcePort = 2103, DetectorId = "527148" });
        Assert.False(HostBootstrapper.IsValidListenerConfiguration(config));
    }

    [Fact]
    public void OmittedTimeZoneUsesServerLocalZone()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{ "SpeedListenerConfiguration": { "UdpPort": 10089 } }"""));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = configuration.GetSection("SpeedListenerConfiguration").Get<SpeedListenerConfiguration>()!;
        Assert.Equal(TimeZoneInfo.Local.Id, options.EventTimeZoneId);
    }

    [Fact]
    public void GeneratorValidationNamesEachInvalidSettingAndMappingIndex()
    {
        var error = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            HostBootstrapper.BuildGeneratorHost(config => {
                config.UdpPort = 0;
                config.EventTimeZoneId = "invalid-zone";
                config.BatchSize = config.ChannelCapacity + 1;
                config.UntaggedSpeedDetectorMappings = new() { new() { SourceAddress = "bad", SourcePort = 0, DetectorId = "bad" } };
            }));
        foreach (var setting in new[] { "UdpPort", "EventTimeZoneId", "BatchSize", "UntaggedSpeedDetectorMappings[0].SourceAddress",
                     "UntaggedSpeedDetectorMappings[0].SourcePort", "UntaggedSpeedDetectorMappings[0].DetectorId" })
            Assert.Contains(error.Failures, message => message.Contains(setting));
    }

    [Fact]
    public void ValidationExplainsShutdownOverflowAndBudgetFailures()
    {
        var config = ValidConfiguration();
        config.WriteTimeout = TimeSpan.MaxValue;
        var validator = new SpeedListenerConfigurationValidator();
        Assert.Contains(validator.Validate(null, config).Failures!, failure =>
            failure.Contains("WriteTimeout") && failure.Contains("supported duration"));
        config.WriteTimeout = TimeSpan.FromSeconds(10);
        Assert.Contains(validator.Validate(null, config).Failures!, failure =>
            failure.Contains("ShutdownFlushTimeout") && failure.Contains("retry delays"));
    }

    [Fact]
    public void GeneratorMappingsAndLocationZonesUseTheSameSingletonSnapshot()
    {
        using var host = HostBootstrapper.BuildGeneratorHost();
        Assert.Same(host.Services.GetRequiredService<SpeedListener.Services.IDeviceMappingProvider>(),
            host.Services.GetRequiredService<SpeedListener.Services.ILocationTimeZoneProvider>());
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
