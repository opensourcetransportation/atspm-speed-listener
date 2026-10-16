namespace SpeedListener.Publishing;

/// <summary>Signals repeated database rejection of data for one device.</summary>
public sealed class PoisonDeviceException(int deviceId, int consecutiveDrops, Exception innerException)
    : Exception($"Device {deviceId} reached {consecutiveDrops} consecutive poison-batch drops.", innerException)
{
    /// <summary>Gets the repeatedly rejected device.</summary>
    public int DeviceId { get; } = deviceId;
    /// <summary>Gets the number of consecutive drops.</summary>
    public int ConsecutiveDrops { get; } = consecutiveDrops;
}
