using System.Buffers.Binary;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal static class RpcRecordStream
{
    internal const int MaxRecordLength = 64 * 1024 * 1024;

    internal static async Task<byte[]> ReceiveAsync(
        Stream stream,
        CancellationToken ct,
        TimeSpan completionTimeout = default)
    {
        using var aggregate = new MemoryStream();
        var header = new byte[4];
        var last = false;
        CancellationTokenSource? completionCts = null;

        try
        {
            CancellationToken recordToken;
            if (completionTimeout > TimeSpan.Zero)
            {
                // Idle connections may wait indefinitely for the next record. Start
                // the completion deadline only after the first byte arrives.
                await stream.ReadExactlyAsync(header.AsMemory(0, 1), ct);
                completionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                completionCts.CancelAfter(completionTimeout);
                recordToken = completionCts.Token;
                await stream.ReadExactlyAsync(header.AsMemory(1), recordToken);
            }
            else
            {
                recordToken = ct;
                await stream.ReadExactlyAsync(header, recordToken);
            }

            while (!last)
            {
                var marker = BinaryPrimitives.ReadUInt32BigEndian(header);
                last = (marker & 0x8000_0000u) != 0;
                var length = (int)(marker & 0x7FFF_FFFF);
                ValidateLength(length, aggregate.Length);

                var fragment = new byte[length];
                await stream.ReadExactlyAsync(fragment, recordToken);
                aggregate.Write(fragment, 0, length);

                if (!last)
                    await stream.ReadExactlyAsync(header, recordToken);
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new NfsException("Truncated RPC record.", ex);
        }
        catch (OperationCanceledException ex) when (completionCts?.IsCancellationRequested == true &&
                                                     !ct.IsCancellationRequested)
        {
            throw new NfsException(
                $"RPC reply record did not complete within {completionTimeout}.",
                new IOException("The RPC reply record was incomplete.", ex));
        }
        finally
        {
            completionCts?.Dispose();
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
