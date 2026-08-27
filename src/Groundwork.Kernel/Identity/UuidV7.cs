using System.Security.Cryptography;

namespace Groundwork.Kernel;

/// <summary>
/// RFC 9562 §5.7 UUID version 7 construction: a 48-bit big-endian Unix millisecond timestamp
/// followed by 74 random bits, with the version and variant fields fixed.
/// </summary>
/// <remarks>
/// <para>
/// The BCL grew <c>Guid.CreateVersion7</c> in .NET 9, so it is unavailable on the <c>net8.0</c>
/// target. Groundwork deliberately does <em>not</em> call the BCL method on <c>net10.0</c> and a
/// hand-written one on <c>net8.0</c>: identity generation would then be two implementations whose
/// agreement nobody verifies. Both targets run this single implementation, and
/// <c>Groundwork.Kernel.Tests</c> pins the resulting layout on each target.
/// </para>
/// </remarks>
internal static class UuidV7
{
    internal static Guid Create(DateTimeOffset timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timestamp, DateTimeOffset.UnixEpoch);

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var unixMilliseconds = timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMilliseconds >> 40);
        bytes[1] = (byte)(unixMilliseconds >> 32);
        bytes[2] = (byte)(unixMilliseconds >> 24);
        bytes[3] = (byte)(unixMilliseconds >> 16);
        bytes[4] = (byte)(unixMilliseconds >> 8);
        bytes[5] = (byte)unixMilliseconds;

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
