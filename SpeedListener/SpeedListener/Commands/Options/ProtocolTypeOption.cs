#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.Commands.Options/ProtocolTypeOption.cs
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

using System.CommandLine;
using System.Net.Sockets;

namespace SpeedListener.Commands.Options;

/// <summary>
/// Command-line option for specifying the network protocol (UDP or TCP).
/// </summary>
public class ProtocolTypeOption : Option<ProtocolType?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolTypeOption"/> class.
    /// </summary>
    public ProtocolTypeOption() : base(
        aliases: new[] { "--protocol", "-pr", "--protocol-type" },
        description: "Network transmission protocol (Udp or Tcp)")
    {
        ArgumentHelpName = "udp|tcp";
    }
}
