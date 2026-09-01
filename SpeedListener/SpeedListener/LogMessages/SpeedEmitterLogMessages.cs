#region license
// Copyright 2026 Utah Departement of Transportation
// for atspm-speed-listener - SpeedListener.LogMessages/SpeedEmitterLogMessages.cs
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
using System.Net.Sockets;

namespace SpeedListener.LogMessages;

/// <summary>
/// Source-generated structured log messages for speed emitter operations.
/// </summary>
public partial class SpeedEmitterLogMessages
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedEmitterLogMessages"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public SpeedEmitterLogMessages(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Logs when the speed emitter service starts.
    /// </summary>
    /// <param name="protocol">The protocol type used (UDP or TCP).</param>
    /// <param name="host">The target listener host.</param>
    /// <param name="port">The target listener port.</param>
    /// <param name="intervalMs">The packet transmission interval in milliseconds.</param>
    [LoggerMessage(EventId = 2001, EventName = "Starting Emitter", Level = LogLevel.Information, Message = "Emitter starting → {protocol} → {host}:{port} @ {intervalMs}ms")]
    public partial void StartingEmitter(ProtocolType protocol, string host, int port, int intervalMs);

    /// <summary>
    /// Logs when no speed sensor devices are found in the configuration repository.
    /// </summary>
    [LoggerMessage(EventId = 2002, EventName = "No Speed Sensors Found", Level = LogLevel.Error, Message = "No devices found for DeviceTypes.SpeedSensor – aborting emitter.")]
    public partial void NoSpeedSensorsFound();

    /// <summary>
    /// Logs when speed sensor devices are successfully loaded from the repository.
    /// </summary>
    /// <param name="count">The number of devices loaded.</param>
    /// <param name="deviceIds">The comma-separated device identifiers.</param>
    [LoggerMessage(EventId = 2003, EventName = "Devices Loaded", Level = LogLevel.Information, Message = "Loaded {count} devices: {deviceIds}")]
    public partial void DevicesLoaded(int count, string deviceIds);

    /// <summary>
    /// Logs when a speed data packet is successfully sent.
    /// </summary>
    /// <param name="protocol">The protocol type used.</param>
    /// <param name="sensorId">The sensor identifier.</param>
    /// <param name="mph">The speed in miles per hour.</param>
    /// <param name="kph">The speed in kilometers per hour.</param>
    /// <param name="timestamp">The timestamp of transmission.</param>
    [LoggerMessage(EventId = 2004, EventName = "Packet Sent", Level = LogLevel.Information, Message = "Sent {protocol} packet [{sensorId}, {mph}mph/{kph}kph] at {timestamp}")]
    public partial void PacketSent(ProtocolType protocol, string sensorId, int mph, int kph, DateTime timestamp);

    /// <summary>
    /// Logs a warning when a TCP connection attempt fails and will retry.
    /// </summary>
    /// <param name="attempt">The current attempt number.</param>
    /// <param name="sensorId">The target sensor identifier.</param>
    /// <param name="ex">The socket exception that occurred.</param>
    [LoggerMessage(EventId = 2005, EventName = "TCP Attempt Failed", Level = LogLevel.Warning, Message = "TCP attempt {attempt} failed for SensorId {sensorId}; retrying in 200ms")]
    public partial void TcpAttemptFailed(int attempt, string sensorId, Exception ex);

    /// <summary>
    /// Logs an error when sending a packet fails.
    /// </summary>
    /// <param name="sensorId">The sensor identifier.</param>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(EventId = 2006, EventName = "Packet Send Failed", Level = LogLevel.Error, Message = "Error sending packet for SensorId {sensorId}; will continue with next device")]
    public partial void PacketSendFailed(string sensorId, Exception ex);

    /// <summary>
    /// Logs when the speed emitter service stops.
    /// </summary>
    [LoggerMessage(EventId = 2007, EventName = "Emitter Stopping", Level = LogLevel.Information, Message = "Emitter stopping.")]
    public partial void EmitterStopping();
}
