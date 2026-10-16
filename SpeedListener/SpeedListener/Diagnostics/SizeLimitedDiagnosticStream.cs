using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SpeedListener.Tests")]

namespace SpeedListener.Diagnostics;

// This is only the opt-in startup capture, not an operational logging provider.
// Reuse the existing file so service accounts need no directory/rotation permissions.
internal sealed class SizeLimitedDiagnosticStream : Stream
{
    internal const long DefaultMaxBytes = 10 * 1024 * 1024;
    private readonly Stream _file;
    private readonly long _maxBytes;

    internal SizeLimitedDiagnosticStream(Stream file, long maxBytes = DefaultMaxBytes)
    {
        if (!file.CanWrite || !file.CanSeek) throw new ArgumentException("A writable seekable stream is required.", nameof(file));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _file = file;
        _maxBytes = maxBytes;
        if (_file.Length > _maxBytes) _file.SetLength(0);
        _file.Position = _file.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > _maxBytes)
        {
            buffer = buffer[^checked((int)_maxBytes)..];
            // StreamWriter supplies UTF-8. Do not retain a partial leading code point.
            while (!buffer.IsEmpty && (buffer[0] & 0xC0) == 0x80) buffer = buffer[1..];
        }
        if (_file.Position + buffer.Length > _maxBytes || _file.Length > _maxBytes)
        {
            _file.SetLength(0);
            _file.Position = 0;
        }
        _file.Write(buffer);
    }

    public override void Flush() => _file.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _file.CanWrite;
    public override long Length => _file.Length;
    public override long Position { get => _file.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
