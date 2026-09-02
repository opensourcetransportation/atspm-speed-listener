using Newtonsoft.Json.Linq;
using System.ComponentModel.DataAnnotations;

namespace SpeedListener.Publishing;

/// <summary>A batch of speed events for one ATSPM device and location.</summary>
public sealed class EventBatchEnvelope
{
    /// <summary>Gets or sets the ATSPM location identifier.</summary>
    [Required]
    public string LocationIdentifier { get; set; } = default!;
    /// <summary>Gets or sets the ATSPM device identifier.</summary>
    [Required]
    public int DeviceId { get; set; }
    /// <summary>Gets or sets the event data type.</summary>
    [Required]
    public string DataType { get; set; } = default!;
    /// <summary>Gets or sets the earliest event timestamp.</summary>
    [Required]
    public DateTime Start { get; set; }
    /// <summary>Gets or sets the latest event timestamp.</summary>
    [Required]
    public DateTime End { get; set; }
    /// <summary>Gets or sets the serialized event collection.</summary>
    [Required]
    public JToken Items { get; set; } = default!;
}
