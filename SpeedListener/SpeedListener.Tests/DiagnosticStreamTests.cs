using System.Text;
using SpeedListener.Diagnostics;

namespace SpeedListener.Tests;

public sealed class DiagnosticStreamTests
{
    [Fact]
    public void RepeatedUnicodeWrites_ResetAtLimitAndKeepRecentOutput()
    {
        using var file = new MemoryStream();
        using var limited = new SizeLimitedDiagnosticStream(file, 128);
        using var writer = new StreamWriter(limited, new UTF8Encoding(false)) { AutoFlush = true };
        for (var index = 0; index < 100; index++)
        {
            writer.WriteLine($"packet {index}: café 🚦");
            Assert.InRange(file.Length, 1, 128);
        }
        var text = new UTF8Encoding(false, true).GetString(file.ToArray());
        Assert.Contains("packet 99: café 🚦", text);
        Assert.DoesNotContain("packet 0:", text);
    }

    [Fact]
    public void ExistingCapture_AppendsBelowLimitAndClearsOversizedFile()
    {
        using var file = new MemoryStream();
        file.Write(Encoding.UTF8.GetBytes("existing"));
        using (var limited = new SizeLimitedDiagnosticStream(file, 20))
        {
            limited.Write(Encoding.UTF8.GetBytes(" new"));
            Assert.Equal("existing new", Encoding.UTF8.GetString(file.ToArray()));
        }
        using var oversized = new MemoryStream();
        oversized.Write(new byte[1000]);
        using var reopened = new SizeLimitedDiagnosticStream(oversized, 20);
        Assert.Equal(0, oversized.Length);
        reopened.Write(Encoding.UTF8.GetBytes("recent"));
        Assert.Equal("recent", Encoding.UTF8.GetString(oversized.ToArray()));
    }

    [Fact]
    public void OversizedSingleWrite_RetainsValidUtf8WithinByteLimit()
    {
        using var file = new MemoryStream();
        using var limited = new SizeLimitedDiagnosticStream(file, 31);
        limited.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("🚦", 100)) + "end"));
        Assert.InRange(file.Length, 1, 31);
        Assert.EndsWith("end", new UTF8Encoding(false, true).GetString(file.ToArray()));
    }
}
