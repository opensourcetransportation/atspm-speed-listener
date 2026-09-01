#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.BackgroundServices/SpeedEmitterBackgroundService.cs
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using System.Net.Sockets;
using System.Text;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Repositories.ConfigurationRepositories;

namespace SpeedListener.BackgroundServices
{
    /// <summary>
    /// Background service that emits test speed sensor data to a configured listener host.
    /// </summary>
    /// <param name="options">The speed emitter configuration options.</param>
    /// <param name="log">The logger instance.</param>
    /// <param name="repo">The ATSPM device repository.</param>
    public class SpeedEmitterBackgroundService(IOptions<SpeedEmitterConfiguration> options,
        ILogger<SpeedEmitterBackgroundService> log,
        IDeviceRepository repo) : BackgroundService
    {
        private readonly SpeedEmitterConfiguration _options = options.Value;
        private readonly ILogger<SpeedEmitterBackgroundService> _log = log;
        private readonly IDeviceRepository _repo = repo;

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation(
              "Emitter starting → {Proto} → {Host}:{Port} @ {Ms}ms",
              _options.ProtocolType, _options.ListenerHost, _options.ListenerPort, _options.IntervalMilliseconds);

            var devices = _repo.GetList()
                .Where(d => d.DeviceType == DeviceTypes.SpeedSensor)
                .ToList();

            if (devices.Count == 0)
            {
                _log.LogError(
                  "No devices found for DeviceTypes.SpeedSensor – aborting emitter.");
                return;
            }

            _log.LogInformation(
              "Loaded {Count} devices: {Ids}",
              devices.Count,
              string.Join(", ", devices.Select(d => d.DeviceIdentifier)));

            while (!stoppingToken.IsCancellationRequested)
            {
                var device = devices[Random.Shared.Next(devices.Count)];
                var sensorId = device.DeviceIdentifier;
                var mph = Random.Shared.Next(20, 80);
                var kph = (int)(mph * 1.609);
                var buffer = new byte[16];
                buffer[8] = (byte)mph;
                buffer[9] = (byte)kph;
                var idFixed = sensorId.PadRight(6).Substring(0, 6);
                var idBytes = Encoding.ASCII.GetBytes(idFixed);
                Array.Copy(idBytes, 0, buffer, 10, idBytes.Length);

                try
                {
                    if (_options.ProtocolType == ProtocolType.Udp)
                    {
                        using var udp = new UdpClient();
                        await udp.SendAsync(buffer, buffer.Length, _options.ListenerHost, _options.ListenerPort);
                    }
                    else
                    {
                        var sent = false;
                        for (int attempt = 1; attempt <= 3 && !sent; attempt++)
                        {
                            try
                            {
                                using var tcp = new TcpClient();
                                await tcp.ConnectAsync(_options.ListenerHost, _options.ListenerPort);

                                var stream = tcp.GetStream();
                                await stream.WriteAsync(buffer, 0, buffer.Length, stoppingToken);
                                tcp.Client.Shutdown(SocketShutdown.Send);

                                sent = true;
                            }
                            catch (SocketException ex)
                            {
                                _log.LogWarning(
                                    ex,
                                    "TCP attempt {Attempt} failed for SensorId {Sensor}; retrying in 200ms",
                                    attempt, sensorId);
                                await Task.Delay(200, stoppingToken);
                            }
                        }

                        if (!sent)
                            throw new SocketException((int)SocketError.NotConnected);
                    }

                    _log.LogInformation(
                      "Sent {Protocol} packet [{Sensor}, {Mph}mph/{Kph}kph] at {Time}",
                      _options.ProtocolType, sensorId, mph, kph, DateTime.UtcNow);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(
                      ex,
                      "Error sending packet for SensorId {Sensor}; will continue with next device",
                      sensorId);
                }

                try
                {
                    await Task.Delay(_options.IntervalMilliseconds, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _log.LogInformation("Emitter stopping.");
        }
    }
}
