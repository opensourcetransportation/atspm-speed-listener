#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.Commands.Options/IntervalOption.cs
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

namespace SpeedListener.Commands.Options;

/// <summary>
/// Command-line option for specifying the packet transmission interval in milliseconds.
/// </summary>
public class IntervalOption : Option<int?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IntervalOption"/> class.
    /// </summary>
    public IntervalOption() : base(
        aliases: new[] { "--interval", "-i", "--interval-ms" },
        description: "Transmission interval in milliseconds (optional, fallbacks to configuration/env)")
    {
    }
}
