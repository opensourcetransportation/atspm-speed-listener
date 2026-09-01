#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.Configuration/SpeedEmitterConfiguration.cs
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

using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using Utah.Udot.NetStandardToolkit.Configuration;

namespace SpeedListener.Configuration
{
    /// <summary>
    /// Configuration options for the speed test data emitter service.
    /// </summary>
    [ConfigurationSection(nameof(SpeedEmitterConfiguration), null)]
    public class SpeedEmitterConfiguration
    {
        /// <summary>
        /// Gets or sets the target speed listener host address or hostname.
        /// </summary>
        [Required]
        public string ListenerHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the target speed listener port number.
        /// </summary>
        public int ListenerPort { get; set; } = 1088;

        /// <summary>
        /// Gets or sets the network transmission protocol (UDP or TCP).
        /// </summary>
        public ProtocolType ProtocolType { get; set; } = ProtocolType.Udp;

        /// <summary>
        /// Gets or sets the transmission interval in milliseconds between emitted packets.
        /// </summary>
        public int IntervalMilliseconds { get; set; } = 100;
    }
}
