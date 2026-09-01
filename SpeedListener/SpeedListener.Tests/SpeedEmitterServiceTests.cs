#region license
// Copyright 2026 Utah Departement of Transportation
// for atspm-speed-listener - SpeedListener.Tests/SpeedEmitterServiceTests.cs
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SpeedListener.Configuration;
using SpeedListener.Services;
using System.Net.Sockets;
using System.Text;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Repositories.ConfigurationRepositories;

namespace SpeedListener.Tests;

/// <summary>
/// Unit tests for <see cref="SpeedEmitterService"/>.
/// </summary>
public class SpeedEmitterServiceTests
{
    private readonly Mock<IDeviceRepository> _deviceRepositoryMock;
    private readonly Mock<ILogger<SpeedEmitterService>> _loggerMock;
    private readonly SpeedEmitterConfiguration _config;
    private readonly IOptions<SpeedEmitterConfiguration> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedEmitterServiceTests"/> class.
    /// </summary>
    public SpeedEmitterServiceTests()
    {
        _deviceRepositoryMock = new Mock<IDeviceRepository>();
        _loggerMock = new Mock<ILogger<SpeedEmitterService>>();
        _loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _config = new SpeedEmitterConfiguration
        {
            ListenerHost = "127.0.0.1",
            ListenerPort = 1088,
            ProtocolType = ProtocolType.Udp,
            IntervalMilliseconds = 50
        };
        _options = Options.Create(_config);
    }

    /// <summary>
    /// Verifies that <see cref="SpeedEmitterService.CreateSpeedPacket"/> creates a 16-byte buffer with correct byte alignments.
    /// </summary>
    [Fact]
    public void CreateSpeedPacket_StandardValues_ReturnsExpected16ByteBuffer()
    {
        var service = new SpeedEmitterService(_options, _deviceRepositoryMock.Object, _loggerMock.Object);
        var sensorId = "1001";
        var mph = 45;
        var kph = 72;

        var result = service.CreateSpeedPacket(sensorId, mph, kph);

        Assert.NotNull(result);
        Assert.Equal(16, result.Length);
        Assert.Equal(mph, result[8]);
        Assert.Equal(kph, result[9]);

        var extractedId = Encoding.ASCII.GetString(result, 10, 6);
        Assert.Equal("1001  ", extractedId);
    }

    /// <summary>
    /// Verifies that <see cref="SpeedEmitterService.CreateSpeedPacket"/> correctly truncates sensor IDs exceeding 6 characters.
    /// </summary>
    [Fact]
    public void CreateSpeedPacket_LongSensorId_TruncatesTo6Characters()
    {
        var service = new SpeedEmitterService(_options, _deviceRepositoryMock.Object, _loggerMock.Object);
        var sensorId = "SENSOR_ABC_123";
        var mph = 60;
        var kph = 96;

        var result = service.CreateSpeedPacket(sensorId, mph, kph);

        Assert.Equal(16, result.Length);
        var extractedId = Encoding.ASCII.GetString(result, 10, 6);
        Assert.Equal("SENSOR", extractedId);
    }

    /// <summary>
    /// Verifies that <see cref="SpeedEmitterService.CreateSpeedPacket"/> handles empty or null sensor ID strings gracefully.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateSpeedPacket_EmptyOrWhitespaceId_PadsSpaces(string sensorId)
    {
        var service = new SpeedEmitterService(_options, _deviceRepositoryMock.Object, _loggerMock.Object);

        var result = service.CreateSpeedPacket(sensorId, 30, 48);

        Assert.Equal(16, result.Length);
        Assert.Equal(30, result[8]);
        Assert.Equal(48, result[9]);
        var extractedId = Encoding.ASCII.GetString(result, 10, 6);
        Assert.Equal(6, extractedId.Length);
    }

    /// <summary>
    /// Verifies that <see cref="SpeedEmitterService.EmitSampleAsync"/> logs an error and returns false when repository has no devices.
    /// </summary>
    [Fact]
    public async Task EmitSampleAsync_NoDevicesFound_ReturnsFalse()
    {
        _deviceRepositoryMock.Setup(r => r.GetList()).Returns(new List<Device>().AsQueryable());

        var service = new SpeedEmitterService(_options, _deviceRepositoryMock.Object, _loggerMock.Object);

        var result = await service.EmitSampleAsync(CancellationToken.None);

        Assert.False(result);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="SpeedEmitterService.EmitSampleAsync"/> ignores devices that are not of type SpeedSensor.
    /// </summary>
    [Fact]
    public async Task EmitSampleAsync_OnlyNonSpeedSensorDevices_ReturnsFalse()
    {
        var devices = new List<Device>
        {
            new() { DeviceIdentifier = "CAM01", DeviceType = DeviceTypes.AICamera },
            new() { DeviceIdentifier = "SIG01", DeviceType = DeviceTypes.SignalController }
        };

        _deviceRepositoryMock.Setup(r => r.GetList()).Returns(devices.AsQueryable());

        var service = new SpeedEmitterService(_options, _deviceRepositoryMock.Object, _loggerMock.Object);

        var result = await service.EmitSampleAsync(CancellationToken.None);

        Assert.False(result);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="SpeedEmitterService.EmitSampleAsync"/> emits packet and logs when speed sensors exist.
    /// </summary>
    [Fact]
    public async Task EmitSampleAsync_SpeedSensorsExist_EmitsAndReturnsTrue()
    {
        var devices = new List<Device>
        {
            new() { DeviceIdentifier = "SPD100", DeviceType = DeviceTypes.SpeedSensor }
        };

        _deviceRepositoryMock.Setup(r => r.GetList()).Returns(devices.AsQueryable());

        var service = new SpeedEmitterService(_options, _deviceRepositoryMock.Object, _loggerMock.Object);

        using var listener = new UdpClient(1088);

        var result = await service.EmitSampleAsync(CancellationToken.None);

        Assert.True(result);
    }
}
