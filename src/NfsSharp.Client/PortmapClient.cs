using NfsSharp.Protocol;

namespace NfsSharp.Client;

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

    internal async Task<int> GetTcpPortAsync(uint program, uint version, CancellationToken ct)
    {
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

    internal static void EnsureMapped(int port, string service)
    {
        if (port == 0)
            throw new NfsException($"{service} service is not registered in portmap for TCP.");
    }
}
