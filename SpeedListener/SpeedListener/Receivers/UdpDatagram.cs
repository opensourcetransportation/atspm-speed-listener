using System.Net;

namespace SpeedListener.Receivers;

/// <summary>A received UDP datagram and its receipt metadata.</summary>
public sealed record UdpDatagram(byte[] Buffer, EndPoint RemoteEndPoint, DateTimeOffset ReceivedAt);
