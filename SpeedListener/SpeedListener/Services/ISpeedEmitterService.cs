#region license
// Copyright 2026 Utah Departement of Transportation
// for atspm-speed-listener - SpeedListener.Services/ISpeedEmitterService.cs
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

using System.Net.Sockets;

namespace SpeedListener.Services;

/// <summary>
/// Defines operations for generating and emitting test speed sensor packets.
/// </summary>
public interface ISpeedEmitterService
{
    /// <summary>
    /// Emits a single test speed sample from a configured speed sensor device.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>True if a sample was emitted; false if no devices are available.</returns>
    Task<bool> EmitSampleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Constructs a 16-byte speed sensor packet buffer for the specified speed and sensor identifier.
    /// </summary>
    /// <param name="sensorId">The sensor identifier string.</param>
    /// <param name="mph">The speed in miles per hour.</param>
    /// <param name="kph">The speed in kilometers per hour.</param>
    /// <returns>A 16-byte array containing the formatted speed packet.</returns>
    byte[] CreateSpeedPacket(string sensorId, int mph, int kph);

    /// <summary>
    /// Transmits a raw packet buffer to the specified host and port using UDP or TCP.
    /// </summary>
    /// <param name="buffer">The packet buffer to transmit.</param>
    /// <param name="host">The target listener host address.</param>
    /// <param name="port">The target listener port.</param>
    /// <param name="protocol">The transmission protocol type.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous transmit operation.</returns>
    Task SendPacketAsync(byte[] buffer, string host, int port, ProtocolType protocol, CancellationToken cancellationToken = default);
}
