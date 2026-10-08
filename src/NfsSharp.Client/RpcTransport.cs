using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal sealed class RpcTransport
{
    private readonly IPAddress _address;
    private readonly NfsClientOptions _options;
    private long _generation;

    internal RpcTransport(IPAddress address, NfsClientOptions options)
    {
        _address = address;
        _options = options;
    }

    internal static async Task<RpcTransport> CreateAsync(
        string server,
        NfsClientOptions options,
        CancellationToken ct) =>
        new(await ResolveAddressAsync(server, ct), options);

    internal async Task<RpcConnection> OpenAsync(int port, CancellationToken ct)
    {
        var socket = await ConnectSocketAsync(_address, port, _options.UsePrivilegedSourcePort, ct);
        ApplySocketOptions(socket, _options);
        return new RpcConnection(
            socket,
            Interlocked.Increment(ref _generation),
            _options.MaxOutstandingRpcCallsPerConnection,
            _options.Logger,
            _options.CommandTimeout);
    }

    internal static async Task SendRecordAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (message.Length > RpcRecordStream.MaxRecordLength)
            throw new NfsException($"RPC record length {message.Length} exceeds the configured limit.");

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x8000_0000u | (uint)message.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(message, ct);
        await stream.FlushAsync(ct);
    }

    internal static Task<byte[]> ReceiveRecordAsync(Stream stream, CancellationToken ct) =>
        RpcRecordStream.ReceiveAsync(stream, ct);

    internal static Task<byte[]> ReceiveRecordAsync(Stream stream, CancellationToken ct, TimeSpan completionTimeout) =>
        RpcRecordStream.ReceiveAsync(stream, ct, completionTimeout);

    private static async Task<Socket> ConnectSocketAsync(
        IPAddress address,
        int port,
        bool usePrivilegedSourcePort,
        CancellationToken ct)
    {
        if (usePrivilegedSourcePort)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var sourcePort = 1023 - Random.Shared.Next(0, 512);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var any = address.AddressFamily == AddressFamily.InterNetworkV6
                        ? IPAddress.IPv6Any
                        : IPAddress.Any;
                    socket.Bind(new IPEndPoint(any, sourcePort));
                    await socket.ConnectAsync(address, port, ct);
                    return socket;
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }

        var fallback = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await fallback.ConnectAsync(address, port, ct);
            return fallback;
        }
        catch
        {
            fallback.Dispose();
            throw;
        }
    }

    private static void ApplySocketOptions(Socket socket, NfsClientOptions options)
    {
        if (options.TcpNoDelay)
            socket.NoDelay = true;

        if (!options.TcpKeepAlive)
            return;

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var interval = (int)options.KeepAliveInterval.TotalSeconds;
        if (interval <= 0)
            return;

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, interval);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, interval);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException)
        {
            // Not all platforms support every TCP keepalive option; best-effort.
        }
    }

    private static async Task<IPAddress> ResolveAddressAsync(string server, CancellationToken ct)
    {
        if (IPAddress.TryParse(server, out var direct))
            return direct;

        var addresses = await Dns.GetHostAddressesAsync(server, ct);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new NfsException($"Unable to resolve NFS server: {server}");
    }
}

internal sealed class RpcConnection : IAsyncDisposable
{
    private readonly Socket? _socket;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _inFlightLimit;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pendingCalls = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _recordCompletionTimeout;
    private readonly object _receiveSync = new();
    private Task? _receiveLoop;
    private Exception? _failure;
    private int _disposed;
    private int _pendingCallCount;
    private int _pendingCallHighWaterMark;

    internal RpcConnection(
        Socket socket,
        long generation,
        int maxOutstandingCalls,
        ILogger? logger = null,
        TimeSpan recordCompletionTimeout = default)
    {
        _socket = socket;
        _logger = logger;
        _recordCompletionTimeout = recordCompletionTimeout;
        _inFlightLimit = new SemaphoreSlim(maxOutstandingCalls);
        Stream = new NetworkStream(socket, ownsSocket: false);
        Generation = generation;
    }

    internal RpcConnection(
        Stream stream,
        long generation = 1,
        int maxOutstandingCalls = 32,
        ILogger? logger = null,
        TimeSpan recordCompletionTimeout = default)
    {
        _logger = logger;
        _recordCompletionTimeout = recordCompletionTimeout;
        _inFlightLimit = new SemaphoreSlim(maxOutstandingCalls);
        Stream = stream;
        Generation = generation;
    }

    internal Stream Stream { get; }
    internal long Generation { get; }
    internal bool IsHealthy => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _failure) is null;
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal int PendingCallCount => Volatile.Read(ref _pendingCallCount);
    internal int PendingCallHighWaterMark => Volatile.Read(ref _pendingCallHighWaterMark);

    internal Task WaitForCallSlotAsync(CancellationToken ct) => _inFlightLimit.WaitAsync(ct);
    internal void ReleaseCallSlot() => _inFlightLimit.Release();

    internal bool TryRegister(uint xid, out TaskCompletionSource<byte[]> pending)
    {
        pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _disposed) != 0 || _failure is not null)
            throw _failure ?? new NfsException($"RPC connection generation {Generation} is closed.");

        if (!_pendingCalls.TryAdd(xid, pending))
            return false;

        UpdateHighWaterMark(Interlocked.Increment(ref _pendingCallCount));

        var failure = _failure;
        if (Volatile.Read(ref _disposed) != 0 || failure is not null)
        {
            TryRemovePending(xid, pending);
            throw failure ?? new NfsException($"RPC connection generation {Generation} is closed.");
        }

        return true;
    }

    private void UpdateHighWaterMark(int count)
    {
        var observed = Volatile.Read(ref _pendingCallHighWaterMark);
        while (count > observed)
        {
            var previous = Interlocked.CompareExchange(ref _pendingCallHighWaterMark, count, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    private bool TryRemovePending(uint xid, TaskCompletionSource<byte[]> pending)
    {
        if (!_pendingCalls.TryRemove(new KeyValuePair<uint, TaskCompletionSource<byte[]>>(xid, pending)))
            return false;

        Interlocked.Decrement(ref _pendingCallCount);
        return true;
    }

    internal void RemovePending(uint xid, TaskCompletionSource<byte[]> pending) =>
        TryRemovePending(xid, pending);

    internal async Task<byte[]> SendAndReceiveAsync(
        uint xid,
        TaskCompletionSource<byte[]> pending,
        byte[] request,
        CancellationToken ct)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_failure is { } failure)
                    throw failure;

                // Once a record write begins, call cancellation must interrupt it. A
                // partial record makes the shared byte stream unusable, so fail the
                // connection before returning the caller's cancellation.
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                try
                {
                    await RpcTransport.SendRecordAsync(Stream, request, sendCts.Token);
                    EnsureReceiveLoopStarted();
                }
                catch (OperationCanceledException) when (Volatile.Read(ref _failure) is { } storedFailure)
                {
                    throw storedFailure;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FailConnection(new NfsException(
                        $"RPC send was canceled on connection generation {Generation} after the record write began.",
                        new IOException("The RPC record may be incomplete.")));
                    throw;
                }
                catch (Exception ex)
                {
                    var sendFailure = new NfsException(
                        $"RPC send failed on connection generation {Generation}.", ex);
                    FailConnection(sendFailure);
                    throw Volatile.Read(ref _failure) ?? sendFailure;
                }
            }
            finally
            {
                _sendLock.Release();
            }

            return await pending.Task.WaitAsync(ct);
        }
        finally
        {
            TryRemovePending(xid, pending);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var record = await RpcTransport.ReceiveRecordAsync(
                    Stream,
                    _lifetime.Token,
                    _recordCompletionTimeout);
                if (record.Length < sizeof(uint))
                    throw new NfsException($"RPC reply on connection generation {Generation} is missing its XID.");

                var xid = BinaryPrimitives.ReadUInt32BigEndian(record);
                if (_pendingCalls.TryRemove(xid, out var pending))
                {
                    Interlocked.Decrement(ref _pendingCallCount);
                    pending.TrySetResult(record);
                }
                else
                {
                    _logger?.LogDebug(
                        "Discarded unmatched RPC reply (xid={Xid}, generation={Generation})",
                        xid,
                        Generation);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal connection shutdown.
        }
        catch (Exception ex)
        {
            FailConnection(new NfsException(
                $"RPC receive failed on connection generation {Generation}.", ex));
        }
    }

    private void EnsureReceiveLoopStarted()
    {
        lock (_receiveSync)
            _receiveLoop ??= ReceiveLoopAsync();
    }

    private void FailConnection(Exception failure)
    {
        if (Interlocked.CompareExchange(ref _failure, failure, null) is not null)
            return;

        _lifetime.Cancel();
        try
        {
            Stream.Dispose();
        }
        catch
        {
            // Closing a failed stream is best-effort; the stored failure is authoritative.
        }

        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // Socket disposal is best-effort.
        }

        foreach (var item in _pendingCalls.ToArray())
        {
            if (_pendingCalls.TryRemove(item.Key, out var pending))
            {
                Interlocked.Decrement(ref _pendingCallCount);
                pending.TrySetException(failure);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FailConnection(new NfsException(
            $"RPC connection generation {Generation} was closed.",
            new IOException("The RPC connection was closed.")));
        try
        {
            await Stream.DisposeAsync();
        }
        catch
        {
            // Disposal is best-effort; the owning RPC layer reports call failures.
        }

        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // Disposal is best-effort.
        }

        try
        {
            var receiveLoop = Volatile.Read(ref _receiveLoop);
            if (receiveLoop is not null)
                await receiveLoop;
        }
        catch
        {
            // The receive loop owns and reports its pending-call failures.
        }

        _lifetime.Dispose();
    }
}
