#region license
// Copyright 2026 Utah Departement of Transportation
// for SpeedListener - SpeedListener.Commands.Options/HostOption.cs
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
/// Command-line option for specifying the target speed listener host address or hostname.
/// </summary>
public class HostOption : Option<string?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HostOption"/> class.
    /// </summary>
    public HostOption() : base(
        aliases: new[] { "--host", "-h", "--listener-host" },
        description: "Target listener host address or hostname (optional, fallbacks to configuration/env)")
    {
    }
}
