using SpeedListener.Receivers;

namespace SpeedListener.Parsing;

/// <summary>Parses raw speed sensor datagrams.</summary>
public interface ISpeedPacketParser
{
    /// <summary>Parses a received datagram.</summary>
    SpeedPacketParseResult Parse(UdpDatagram datagram);
}
