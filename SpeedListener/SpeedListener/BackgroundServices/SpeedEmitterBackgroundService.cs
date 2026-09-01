#region license
// Copyright 2026 Utah Departement of Transportation
// for atspm-speed-listener - SpeedListener.BackgroundServices/SpeedEmitterBackgroundService.cs
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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.LogMessages;
using SpeedListener.Services;

namespace SpeedListener.BackgroundServices;

/// <summary>
/// Background service that periodically triggers the speed emitter service to send test sensor data.
/// </summary>
/// <param name="options">The speed emitter configuration options.</param>
/// <param name="emitterService">The speed emitter service.</param>
/// <param name="logger">The logger instance.</param>
public class SpeedEmitterBackgroundService(
    IOptions<SpeedEmitterConfiguration> options,
    ISpeedEmitterService emitterService,
    ILogger<SpeedEmitterBackgroundService> logger) : BackgroundService
{
    private readonly SpeedEmitterConfiguration _options = options.Value;
    private readonly ISpeedEmitterService _emitterService = emitterService;
    private readonly SpeedEmitterLogMessages _log = new SpeedEmitterLogMessages(logger);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.StartingEmitter(_options.ProtocolType, _options.ListenerHost, _options.ListenerPort, _options.IntervalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var emitted = await _emitterService.EmitSampleAsync(stoppingToken);
            if (!emitted)
            {
                break;
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

        _log.EmitterStopping();
    }
}
