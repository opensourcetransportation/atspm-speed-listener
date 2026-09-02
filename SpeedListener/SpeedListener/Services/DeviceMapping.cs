namespace SpeedListener.Services;

/// <summary>Maps a wire-level sensor identifier to an ATSPM device and location.</summary>
public sealed record DeviceMapping(int DeviceId, string LocationIdentifier);
