using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>RPC portmap (portmapper) client for resolving program ports (RFC 1833).</summary>
internal sealed class PortmapClient
{
    private const uint IpProtoTcp = 6;
    private readonly RpcClient _rpcClient;
    private readonly int _portmapPort;

    internal PortmapClient(RpcClient rpcClient, int portmapPort)
    {
        _rpcClient = rpcClient;
        _portmapPort = portmapPort;
    }

    /// <summary>portmap GETPORT — resolve the TCP port registered for a program/version.</summary>
    internal async Task<int> GetTcpPortAsync(uint program, uint version, CancellationToken ct)
    {
        // GETPORT args: program, version, protocol (IPPROTO_TCP), reserved port (0).
        var writer = new XdrWriter();
        writer.UInt(program);
        writer.UInt(version);
        writer.UInt(IpProtoTcp);
        writer.UInt(0);

        var reader = await _rpcClient.CallWithOwnedConnectionAsync(
            _portmapPort,
            NfsRpcConstants.ProgPortmap,
            NfsRpcConstants.VerPortmap,
            NfsRpcConstants.PmapGetPort,
            writer.ToArray(),
            ct);
        var port = reader.UInt();
        if (port > ushort.MaxValue)
        {
            throw new NfsException(
                $"Portmap returned invalid TCP port {port} for program {program} version {version}.");
        }

        return (int)port;
    }

    /// <summary>Throw when portmap reported no TCP registration for the service.</summary>
    internal static void EnsureMapped(int port, string service)
    {
        if (port == 0)
            throw new NfsException($"{service} service is not registered in portmap for TCP.");
    }
}
