#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener/HostBootstrapper.cs
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using Utah.Udot.Atspm.Infrastructure.Extensions;

namespace SpeedListener;

/// <summary>
/// Static bootstrapper to initialize, configure, and execute the generic host for speed listener and emitter services.
/// </summary>
public static class HostBootstrapper
{
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
            services.AddHostedService<TService>();
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
}
