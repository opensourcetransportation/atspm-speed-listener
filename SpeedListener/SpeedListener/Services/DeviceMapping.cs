namespace SpeedListener.Services;

/// <summary>Identifies the selected speed device on a current location version.</summary>
public sealed record DeviceMapping(int DeviceId, string LocationIdentifier);
