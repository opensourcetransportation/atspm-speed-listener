#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.Commands/EmmitterCommand.cs
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

using SpeedListener.BackgroundServices;
using SpeedListener.Commands.Options;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace SpeedListener.Commands;

/// <summary>
/// Command for starting the test speed data emitter service.
/// </summary>
public class EmmitterCommand : Command
{
    /// <summary>
    /// Gets the shared target host command-line option.
    /// </summary>
    public static readonly HostOption HostOption = new();

    /// <summary>
    /// Gets the shared target port command-line option.
    /// </summary>
    public static readonly PortOption PortOption = new();

    /// <summary>
    /// Gets the shared protocol type command-line option.
    /// </summary>
    public static readonly ProtocolTypeOption ProtocolTypeOption = new();

    /// <summary>
    /// Gets the shared interval milliseconds command-line option.
    /// </summary>
    public static readonly IntervalOption IntervalOption = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EmmitterCommand"/> class.
    /// </summary>
    public EmmitterCommand() : base("emitter", "Starts transmitting test data to configured host, port and protocol")
    {
        AddAlias("Emitter");

        AddOption(HostOption);
        AddOption(PortOption);
        AddOption(ProtocolTypeOption);
        AddOption(IntervalOption);

        this.SetHandler(async (context) =>
        {
            await RunEmitterAsync(context);
        });
    }

    /// <summary>
    /// Utility method to parse all shared options into a unified configuration and start the host.
    /// </summary>
    /// <param name="context">The invocation context containing parsed command-line results.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected static async Task RunEmitterAsync(InvocationContext context)
    {
        await HostBootstrapper.RunHostAsync<SpeedEmitterBackgroundService>(options =>
        {
            if (IsSpecified(context.ParseResult, HostOption))
            {
                var host = context.ParseResult.GetValueForOption(HostOption);
                if (!string.IsNullOrWhiteSpace(host))
                {
                    options.ListenerHost = host;
                }
            }

            if (IsSpecified(context.ParseResult, PortOption))
            {
                var port = context.ParseResult.GetValueForOption(PortOption);
                if (port.HasValue)
                {
                    options.ListenerPort = port.Value;
                }
            }

            if (IsSpecified(context.ParseResult, ProtocolTypeOption))
            {
                var protocol = context.ParseResult.GetValueForOption(ProtocolTypeOption);
                if (protocol.HasValue)
                {
                    options.ProtocolType = protocol.Value;
                }
            }

            if (IsSpecified(context.ParseResult, IntervalOption))
            {
                var interval = context.ParseResult.GetValueForOption(IntervalOption);
                if (interval.HasValue)
                {
                    options.IntervalMilliseconds = interval.Value;
                }
            }
        });
    }

    private static bool IsSpecified(ParseResult parseResult, Option option)
    {
        return parseResult.FindResultFor(option) is { } res && !res.IsImplicit;
    }
}
