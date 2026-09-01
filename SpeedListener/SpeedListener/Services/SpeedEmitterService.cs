#region license
// Copyright 2026 Utah Departement of Transportation
// for atspm-speed-listener - SpeedListener.Services/SpeedEmitterService.cs
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
using SpeedListener.Configuration;
using SpeedListener.LogMessages;
using System.Net.Sockets;
using System.Text;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Repositories.ConfigurationRepositories;

namespace SpeedListener.Services;

/// <summary>
/// Service responsible for generating speed sensor test payloads and transmitting them over UDP or TCP.
/// </summary>
public class SpeedEmitterService : ISpeedEmitterService
{
    private readonly SpeedEmitterConfiguration _options;
    private readonly IDeviceRepository _repo;
    private readonly SpeedEmitterLogMessages _log;
    private List<Device>? _cachedDevices;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedEmitterService"/> class.
    /// </summary>
    /// <param name="options">The speed emitter configuration options.</param>
    /// <param name="repo">The ATSPM device repository.</param>
    /// <param name="logger">The logger instance.</param>
    public SpeedEmitterService(
        IOptions<SpeedEmitterConfiguration> options,
        IDeviceRepository repo,
        ILogger<SpeedEmitterService> logger)
    {
        _options = options.Value;
        _repo = repo;
        _log = new SpeedEmitterLogMessages(logger);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedEmitterService"/> class with explicit log messages.
    /// </summary>
    /// <param name="options">The speed emitter configuration options.</param>
    /// <param name="repo">The ATSPM device repository.</param>
    /// <param name="log">The structured log messages instance.</param>
    public SpeedEmitterService(
        IOptions<SpeedEmitterConfiguration> options,
        IDeviceRepository repo,
        SpeedEmitterLogMessages log)
    {
        _options = options.Value;
        _repo = repo;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<bool> EmitSampleAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedDevices == null)
        {
            _cachedDevices = _repo.GetList()
                .Where(d => d.DeviceType == DeviceTypes.SpeedSensor)
                .ToList();

            if (_cachedDevices.Count == 0)
            {
                _log.NoSpeedSensorsFound();
                return false;
            }

            _log.DevicesLoaded(
                _cachedDevices.Count,
                string.Join(", ", _cachedDevices.Select(d => d.DeviceIdentifier)));
        }

        if (_cachedDevices.Count == 0)
        {
            return false;
        }

        var device = _cachedDevices[Random.Shared.Next(_cachedDevices.Count)];
        var sensorId = device.DeviceIdentifier;
        var mph = Random.Shared.Next(20, 80);
        var kph = (int)(mph * 1.609);
        var buffer = CreateSpeedPacket(sensorId, mph, kph);

        try
        {
            await SendPacketAsync(buffer, _options.ListenerHost, _options.ListenerPort, _options.ProtocolType, cancellationToken);
            _log.PacketSent(_options.ProtocolType, sensorId, mph, kph, DateTime.UtcNow);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.PacketSendFailed(sensorId, ex);
            return false;
        }
    }

    /// <inheritdoc/>
    public byte[] CreateSpeedPacket(string sensorId, int mph, int kph)
    {
        var buffer = new byte[16];
        buffer[8] = (byte)mph;
        buffer[9] = (byte)kph;
        var idFixed = (sensorId ?? string.Empty).PadRight(6)[..6];
        var idBytes = Encoding.ASCII.GetBytes(idFixed);
        Array.Copy(idBytes, 0, buffer, 10, idBytes.Length);
        return buffer;
    }

    /// <inheritdoc/>
    public async Task SendPacketAsync(byte[] buffer, string host, int port, ProtocolType protocol, CancellationToken cancellationToken = default)
    {
        if (protocol == ProtocolType.Udp)
        {
            using var udp = new UdpClient();
            await udp.SendAsync(buffer, buffer.Length, host, port);
        }
        else
        {
            var sent = false;
            for (var attempt = 1; attempt <= 3 && !sent; attempt++)
            {
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(host, port);

                    var stream = tcp.GetStream();
                    await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
                    tcp.Client.Shutdown(SocketShutdown.Send);

                    sent = true;
                }
                catch (SocketException ex)
                {
                    _log.TcpAttemptFailed(attempt, host, ex);
                    await Task.Delay(200, cancellationToken);
                }
            }

            if (!sent)
            {
                throw new SocketException((int)SocketError.NotConnected);
            }
        }
    }
}
