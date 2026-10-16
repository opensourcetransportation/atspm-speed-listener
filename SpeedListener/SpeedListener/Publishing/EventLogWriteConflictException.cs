namespace SpeedListener.Publishing;

/// <summary>A single device-hour has an uncertain commit after an outside change.</summary>
public sealed class EventLogWriteConflictException(string message) : Exception(message);
