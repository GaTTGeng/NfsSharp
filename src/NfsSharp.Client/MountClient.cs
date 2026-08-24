using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal sealed class MountClient
{
    private readonly RpcClient _rpcClient;

    internal MountClient(RpcClient rpcClient)
    {
        _rpcClient = rpcClient;
    }

    internal async Task<byte[]> MountAsync(int mountPort, string exportPath, CancellationToken ct)
    {
        var writer = new XdrWriter();
        writer.Str(exportPath);
        var reader = await _rpcClient.CallWithOwnedConnectionAsync(
            mountPort,
            NfsRpcConstants.ProgMount,
            NfsRpcConstants.VerMount,
            NfsRpcConstants.MountMnt,
            writer.ToArray(),
            ct);
        var status = reader.UInt();
        if (status != MountV3Status.Ok)
        {
            throw new NfsException(
                $"MOUNT \"{exportPath}\" failed (mountstat3={MountV3Status.Describe(status)} ({status})).",
                status);
        }

        return reader.Opaque();
    }

    internal async Task<IReadOnlyList<NfsExport>> ListExportsAsync(int mountPort, CancellationToken ct)
    {
        var reader = await _rpcClient.CallWithOwnedConnectionAsync(
            mountPort,
            NfsRpcConstants.ProgMount,
            NfsRpcConstants.VerMount,
            NfsRpcConstants.MountExport,
            Array.Empty<byte>(),
            ct);
        var exports = new List<NfsExport>();
        while (reader.Bool())
        {
            var path = reader.Str();
            var groups = new List<string>();
            while (reader.Bool())
                groups.Add(reader.Str());
            exports.Add(new NfsExport(path, groups));
        }

        return exports;
    }

    internal async Task UnmountAsync(int mountPort, string exportPath, CancellationToken ct)
    {
        var writer = new XdrWriter();
        writer.Str(exportPath);
        await _rpcClient.CallWithOwnedConnectionAsync(
            mountPort,
            NfsRpcConstants.ProgMount,
            NfsRpcConstants.VerMount,
            NfsRpcConstants.MountUmnt,
            writer.ToArray(),
            ct);
    }
}
