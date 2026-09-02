#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener/HostBootstrapper.cs
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpeedListener.BackgroundServices;
using SpeedListener.Configuration;
using SpeedListener.Parsing;
using SpeedListener.Publishing;
using SpeedListener.Receivers;
using SpeedListener.Services;
using Utah.Udot.Atspm.Infrastructure.Extensions;

namespace SpeedListener;

/// <summary>
/// Static bootstrapper to initialize, configure, and execute the generic host for speed listener and emitter services.
/// </summary>
public static class HostBootstrapper
{
    /// <summary>Runs the speed listener host.</summary>
    public static async Task RunListenerHostAsync(Action<SpeedListenerConfiguration> configureAction)
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((hostContext, services) =>
        {
            services.AddOptions<SpeedListenerConfiguration>()
                .Bind(hostContext.Configuration.GetSection(nameof(SpeedListenerConfiguration)))
                .Configure(configureAction)
                .Validate(configuration =>
                    configuration.UdpPort is > 0 and <= 65535 &&
                    configuration.ChannelCapacity > 0 &&
                    configuration.BatchSize > 0 &&
                    configuration.BatchSize <= configuration.ChannelCapacity &&
                    configuration.FlushInterval > TimeSpan.Zero &&
                    configuration.ShutdownFlushTimeout > TimeSpan.Zero &&
                    configuration.ShutdownFlushTimeout > configuration.WriteTimeout &&
                    configuration.ShutdownMaxWriteAttempts > 0 &&
                    configuration.ShutdownMaxWriteAttempts <= configuration.MaxWriteAttempts &&
                    configuration.DeviceMappingRefreshInterval > TimeSpan.Zero &&
                    configuration.ArchiveParallelism > 0 &&
                    configuration.WriteTimeout > TimeSpan.Zero &&
                    configuration.MaxWriteAttempts > 0 &&
                    configuration.PoisonDeviceFailureThreshold > 0 &&
                    configuration.SummaryInterval > TimeSpan.Zero,
                    "Speed listener configuration is missing or invalid.")
                .ValidateOnStart();

            services.AddOptions<HostOptions>()
                .Configure<IOptions<SpeedListenerConfiguration>>((hostOptions, listenerOptions) =>
                    hostOptions.ShutdownTimeout = listenerOptions.Value.ShutdownFlushTimeout + TimeSpan.FromSeconds(5));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<SpeedListenerMetrics>();
            services.AddAtspmDbContext(hostContext);
            services.AddAtspmEFConfigRepositories();
            services.AddAtspmEFEventLogRepositories();
            services.AddSingleton<ISpeedPacketParser, SpeedPacketParser>();
            services.AddSingleton<IDeviceMappingProvider, DeviceMappingProvider>();
            services.AddSingleton<IEventPublisher<EventBatchEnvelope>, DatabaseEventPublisher>();
            services.AddSingleton<ISpeedEventBatchProcessor, SpeedEventBatchProcessor>();
            services.AddSingleton<IUdpDatagramReceiver>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredService<IOptions<SpeedListenerConfiguration>>().Value;
                return new UdpDatagramReceiver(
                    configuration.UdpPort,
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UdpDatagramReceiver>>());
            });
            services.AddHostedService<SpeedListenerBackgroundService>();
        });

        using var host = builder.Build();
        await host.RunAsync();
    }

    /// <summary>
    /// Executes the generic host for a specific emitter service and configuration option.
    /// </summary>
    /// <typeparam name="TService">The target IHostedService class to execute.</typeparam>
    /// <param name="configureAction">The action callback to configure emitter options.</param>
    /// <returns>Returns a task representing the asynchronous execution.</returns>
    public static async Task RunHostAsync<TService>(Action<SpeedEmitterConfiguration> configureAction)
        where TService : class, IHostedService
    {
        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices((hostContext, services) =>
        {
            services.AddOptions<SpeedEmitterConfiguration>()
                .Bind(hostContext.Configuration.GetSection(nameof(SpeedEmitterConfiguration)))
                .Configure(configureAction)
                .Validate(opt =>
                {
                    return !string.IsNullOrWhiteSpace(opt.ListenerHost) &&
                           opt.ListenerPort > 0 &&
                           opt.ListenerPort <= 65535 &&
                           opt.IntervalMilliseconds > 0;
                }, "Required speed emitter configuration options are missing or invalid. Please provide '--host', '--port', '--protocol', and '--interval' options via command line, environment variables (e.g. SpeedEmitterConfiguration__ListenerHost), or appsettings.json.")
                .ValidateOnStart();

            services.AddSingleton(sp => sp.GetRequiredService<IOptions<SpeedEmitterConfiguration>>().Value);
            services.AddAtspmDbContext(hostContext);
            services.AddAtspmEFConfigRepositories();
            services.AddTransient<ISpeedEmitterService, SpeedEmitterService>();
            services.AddHostedService<TService>();
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
}
