#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.Configuration/SpeedListenerConfiguration.cs
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

using Utah.Udot.NetStandardToolkit.Configuration;

namespace SpeedListener.Configuration
{
    /// <summary>
    /// Configuration options for the event and speed listener service.
    /// </summary>
    [ConfigurationSection(nameof(EventListenerConfiguration), null)]
    public class EventListenerConfiguration
    {
        /// <summary>
        /// The URL of your DataApi ingest endpoint, e.g. "https://dataapi:5001/"
        /// </summary>
        public string ApiBaseUrl { get; set; } = default!;

        /// <summary>
        /// The URL of your DataApi ingest endpoint, e.g. "https://dataapi:5001/"
        /// </summary>
        public string ApiEndPoint { get; set; } = default!;

        /// <summary>
        /// How many events to buffer before POSTing.
        /// </summary>
        public int BatchSize { get; set; } = 50_000;

        /// <summary>
        /// The UDP port to listen on.
        /// </summary>
        public int UdpPort { get; set; } = 10088;

        /// <summary>
        /// The UDP port to listen on.
        /// </summary>
        public int threads { get; set; } = 50;
    }
}
