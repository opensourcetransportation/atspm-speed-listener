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

using Google.Cloud.Diagnostics.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using SpeedListener.BackgroundServices;
using SpeedListener.Configuration;
using SpeedListener.LoadTesting;
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
    /// <summary>Builds configuration-only services for generator discovery; starts no listener or writer.</summary>
    public static IHost BuildGeneratorHost(Action<SpeedListenerConfiguration>? configureAction = null)
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var host = Host.CreateDefaultBuilder()
        .UseContentRoot(AppContext.BaseDirectory)
        .ApplyVolumeConfiguration(Path.Combine(AppContext.BaseDirectory, "Configuration"))
        .ConfigureServices((hostContext, services) =>
        {
            services.AddSingleton<IValidateOptions<SpeedListenerConfiguration>, SpeedListenerConfigurationValidator>();
            services.AddOptions<SpeedListenerConfiguration>()
                .Bind(hostContext.Configuration.GetSection(nameof(SpeedListenerConfiguration)))
                .Configure(configureAction ?? (_ => { }))
                .ValidateOnStart();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<SpeedListenerMetrics>();
            services.AddAtspmDbContext(hostContext);
            services.AddSingleton<DeviceMappingProvider>();
            services.AddSingleton<IDeviceMappingProvider>(provider => provider.GetRequiredService<DeviceMappingProvider>());
            services.AddSingleton<ILocationTimeZoneProvider>(provider => provider.GetRequiredService<DeviceMappingProvider>());
            services.AddScoped<DatabaseSpeedTargetProvider>();
        }).Build();
        try
        {
            // Discovery does not start the host, so force options validation here.
            _ = host.Services.GetRequiredService<IOptions<SpeedListenerConfiguration>>().Value;
            return host;
        }
        catch { host.Dispose(); throw; }
    }

    /// <summary>Runs the speed listener host.</summary>
    public static async Task RunListenerHostAsync(Action<SpeedListenerConfiguration> configureAction)
    {
        Program.TraceStartup("Listener command entered; checking Windows service detection");
        Program.TraceStartup($"Windows service detected: {Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()}");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var builder = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseWindowsService(options => options.ServiceName =
                Environment.GetEnvironmentVariable("ATSPM_SERVICE_NAME") ?? "AtspmSpeedListener")
            // Services start in System32; the toolkit resolves relative paths
            // against the working directory, not the host's content root.
            .ApplyVolumeConfiguration(Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
                ? Path.Combine(AppContext.BaseDirectory, "Configuration")
                : "Configuration")
            .ConfigureLogging((hostContext, logging) =>
            {
                Program.TraceStartup("Configuring event logging");
                if (OperatingSystem.IsWindows())
                    TryConfigureWindowsEventLog(logging);

                Program.TraceStartup("Configuring Google logging");
                logging.AddGoogle(hostContext);
                Program.TraceStartup("Logging configured");
            });
        builder.ConfigureServices((hostContext, services) =>
        {
            services.AddSingleton<IValidateOptions<SpeedListenerConfiguration>, SpeedListenerConfigurationValidator>();
            services.AddOptions<SpeedListenerConfiguration>()
                .Bind(hostContext.Configuration.GetSection(nameof(SpeedListenerConfiguration)))
                .Configure(configureAction)
                .ValidateOnStart();

            services.AddOptions<HostOptions>()
                .Configure<IOptions<SpeedListenerConfiguration>>((hostOptions, listenerOptions) =>
                    hostOptions.ShutdownTimeout = listenerOptions.Value.ShutdownFlushTimeout + TimeSpan.FromSeconds(5));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<SpeedListenerMetrics>();
            services.AddAtspmDbContext(hostContext);
            services.AddAtspmEFConfigRepositories();
            services.AddAtspmEFEventLogRepositories();
            services.AddScoped<IEventLogWriter, EfEventLogWriter>();
            services.AddSingleton<ISpeedPacketParser, SpeedPacketParser>();
            services.AddSingleton<DeviceMappingProvider>();
            services.AddSingleton<IDeviceMappingProvider>(provider => provider.GetRequiredService<DeviceMappingProvider>());
            services.AddSingleton<ILocationTimeZoneProvider>(provider => provider.GetRequiredService<DeviceMappingProvider>());
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

        Program.TraceStartup("Building listener host");
        using var host = builder.Build();
        Program.TraceStartup($"Host built; lifetime={host.Services.GetRequiredService<IHostLifetime>().GetType().FullName}; starting host");
        await host.RunAsync();
    }

    /// <summary>Validates operational limits and room for at least one shutdown publish.</summary>
    public static bool IsValidListenerConfiguration(SpeedListenerConfiguration configuration) =>
        new SpeedListenerConfigurationValidator().Validate(null, configuration).Succeeded;

    [SupportedOSPlatform("windows")]
    private static void TryConfigureWindowsEventLog(ILoggingBuilder logging)
    {
        const string preferredLogName = "Atspm";
        const string sourceName = "AtspmSpeedListener";
        try
        {
            if (!EventLog.SourceExists(sourceName))
                EventLog.CreateEventSource(sourceName, preferredLogName);
            // Event sources are machine-wide and can belong to only one log.
            // Honor an existing registration instead of breaking host startup.
            var logName = EventLog.LogNameFromSourceName(sourceName, ".");
            logging.AddEventLog(configuration =>
            {
                configuration.SourceName = sourceName;
                configuration.LogName = logName;
            });
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
        {
            // Event-source discovery/registration requires elevation. Other configured providers remain active.
        }
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
