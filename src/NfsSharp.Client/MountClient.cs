using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>MOUNT protocol v3 client used to obtain the export-root file handle (RFC 1813).</summary>
internal sealed class MountClient
{
    private readonly RpcClient _rpcClient;

    internal MountClient(RpcClient rpcClient)
    {
        _rpcClient = rpcClient;
    }

    /// <summary>MNT — mount an export and return the file handle of its root directory.</summary>
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

    /// <summary>EXPORT — list exported paths and the groups allowed to mount each.</summary>
    internal async Task<IReadOnlyList<NfsExport>> ListExportsAsync(int mountPort, CancellationToken ct)
    {
        var reader = await _rpcClient.CallWithOwnedConnectionAsync(
            mountPort,
            NfsRpcConstants.ProgMount,
            NfsRpcConstants.VerMount,
            NfsRpcConstants.MountExport,
            Array.Empty<byte>(),
            ct);
        // EXPORT result is nested boolean-terminated lists: (path, groups...) then false.
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

    /// <summary>UMNT — drop the server-side mount record for an export.</summary>
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
