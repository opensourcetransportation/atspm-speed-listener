#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener/Program.cs
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

using SpeedListener.Commands;
using System.CommandLine;

namespace SpeedListener;

/// <summary>
/// Entry point class for the ATSPM Speed Listener CLI Utility application.
/// </summary>
public class Program
{
    /// <summary>
    /// Core main execution entry point.
    /// </summary>
    /// <param name="args">The command-line arguments passed to the utility.</param>
    /// <returns>Returns the command-line exit execution code.</returns>
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("ATSPM Speed Listener Utility")
        {
            new EmmitterCommand(),
            new ListenerCommand()
        };

        return await rootCommand.InvokeAsync(args);
    }
}