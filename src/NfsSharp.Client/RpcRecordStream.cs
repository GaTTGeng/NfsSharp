using System.Buffers.Binary;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal static class RpcRecordStream
{
    internal const int MaxRecordLength = 64 * 1024 * 1024;

    internal static async Task<byte[]> ReceiveAsync(Stream stream, CancellationToken ct)
    {
        using var aggregate = new MemoryStream();
        var header = new byte[4];
        var last = false;

        try
        {
            while (!last)
            {
                await stream.ReadExactlyAsync(header, ct);
                var marker = BinaryPrimitives.ReadUInt32BigEndian(header);
                last = (marker & 0x8000_0000u) != 0;
                var length = (int)(marker & 0x7FFF_FFFF);
                ValidateLength(length, aggregate.Length);

                var fragment = new byte[length];
                await stream.ReadExactlyAsync(fragment, ct);
                aggregate.Write(fragment, 0, length);
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new NfsException("Truncated RPC record.", ex);
        }

        return aggregate.ToArray();
    }

    internal static void ValidateLength(int fragmentLength, long accumulatedLength)
    {
        if (fragmentLength < 0 ||
            fragmentLength > MaxRecordLength ||
            accumulatedLength < 0 ||
            accumulatedLength > MaxRecordLength - fragmentLength)
        {
            throw new NfsException(
                $"Invalid RPC record length: accumulated={accumulatedLength}, fragment={fragmentLength}.");
        }
    }
}
