using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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
        return new RpcConnection(socket, Interlocked.Increment(ref _generation));
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

    internal RpcConnection(Socket socket, long generation)
    {
        _socket = socket;
        Stream = new NetworkStream(socket, ownsSocket: false);
        Generation = generation;
    }

    internal RpcConnection(Stream stream, long generation = 1)
    {
        Stream = stream;
        Generation = generation;
    }

    internal Stream Stream { get; }
    internal long Generation { get; }

    public async ValueTask DisposeAsync()
    {
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
    }
}
