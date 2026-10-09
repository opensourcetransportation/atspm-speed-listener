#region license
// Copyright 2026 Utah Departement of Transportation
// Licensed under the Apache License, Version 2.0.
#endregion

using System.CommandLine;
using System.CommandLine.Invocation;

namespace SpeedListener.Commands;

/// <summary>Starts the UDP speed sensor listener.</summary>
public sealed class ListenerCommand : Command
{
    private static readonly Option<int?> PortOption = new(
        aliases: ["--port", "-p"],
        description: "UDP port on which speed sensor packets are received.");

    /// <summary>Initializes the listener command.</summary>
    public ListenerCommand() : base("listener", "Starts the UDP speed sensor listener")
    {
        AddOption(PortOption);
        this.SetHandler(RunListenerAsync);
    }

    private static async Task RunListenerAsync(InvocationContext context)
    {
        await HostBootstrapper.RunListenerHostAsync(configuration =>
        {
            var result = context.ParseResult.FindResultFor(PortOption);
            if (result is not null && !result.IsImplicit)
            {
                var port = context.ParseResult.GetValueForOption(PortOption);
                if (port.HasValue) configuration.UdpPort = port.Value;
            }
        });
    }
}
