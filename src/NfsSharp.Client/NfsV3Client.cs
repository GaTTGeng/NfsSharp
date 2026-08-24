using System.Net;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Managed high-level NFSv3 client.</summary>
public sealed partial class NfsV3Client : IAsyncDisposable
{
    private readonly NfsClientOptions _options;
    private readonly ILogger? _logger;
    private readonly RpcClient _rpcClient;
    private readonly PortmapClient _portmapClient;
    private readonly MountClient _mountClient;
    private readonly RpcSecGssSession _gssSession;
    private readonly NfsV3ProtocolClient _protocolClient;
    private readonly NfsPathResolver _pathResolver;
    private byte[] _rootFh = [];
    private string _exportPath = "";
    private int _mountPort;
    private bool _unmounted;
    private bool _disposed;

    private NfsV3Client(IPAddress address, NfsClientOptions options)
        : this(new RpcTransport(address, options), options) { }

    private NfsV3Client(RpcTransport transport, NfsClientOptions options)
    {
        _options = options;
        _logger = options.Logger;
        var retry = new NfsRetryPolicy(options);
        _gssSession = new RpcSecGssSession(options);
        _rpcClient = new RpcClient(transport, options, retry, _gssSession, RpcAuthSysCredentials.Encode(options));
        _portmapClient = new PortmapClient(_rpcClient, options.PortmapPort);
        _mountClient = new MountClient(_rpcClient);
        _protocolClient = new NfsV3ProtocolClient(_rpcClient, options, new NfsDirectoryCache(options));
        _pathResolver = new NfsPathResolver(
            () => _rootFh,
            _protocolClient.LookupAsync,
            _protocolClient.GetAttributesAsync);
    }

    /// <summary>File handle for the mounted export root.</summary>
    public byte[] RootHandle => _rootFh;

    /// <summary>Resolve a server, mount an export, and open the NFSv3 connection.</summary>
    public static Task<NfsV3Client> ConnectAsync(string server, string exportPath, CancellationToken ct) =>
        ConnectAsync(server, exportPath, NfsClientOptions.Default, ct);

    /// <summary>Resolve a server, mount an export, and open the NFSv3 connection.</summary>
    public static async Task<NfsV3Client> ConnectAsync(
        string server, string exportPath, NfsClientOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server)) throw new NfsException("NFS server is empty.");
        if (string.IsNullOrWhiteSpace(exportPath)) throw new NfsException("NFS export path is empty.");
        options ??= NfsClientOptions.Default;
        options.Validate();
        var client = new NfsV3Client(await RpcTransport.CreateAsync(server, options, ct), options)
        {
            _exportPath = exportPath
        };
        try
        {
            var mountPort = await client._portmapClient.GetTcpPortAsync(
                NfsRpcConstants.ProgMount, NfsRpcConstants.VerMount, ct);
            var nfsPort = await client._portmapClient.GetTcpPortAsync(
                NfsRpcConstants.ProgNfs, NfsRpcConstants.VerNfs, ct);
            PortmapClient.EnsureMapped(mountPort, "mountd");
            PortmapClient.EnsureMapped(nfsPort, "NFS");
            client._mountPort = mountPort;
            client._rootFh = await client._mountClient.MountAsync(mountPort, exportPath, ct);
            await client._rpcClient.ConnectAsync(nfsPort, ct);
            if (options.GssMechanism is not null)
                await client._gssSession.EstablishAsync(server, client._rpcClient, ct);
            client._logger?.LogInformation(
                "NFS mounted {Export} on {Server} (mountd={MountPort}, nfsd={NfsPort})",
                exportPath, server, mountPort, nfsPort);
            return client;
        }
        catch
        {
            await client._rpcClient.DisposeAsync();
            throw;
        }
    }

    /// <summary>List exports advertised by mountd without mounting any export.</summary>
    public static Task<IReadOnlyList<NfsExport>> ListExportsAsync(string server, CancellationToken ct) =>
        ListExportsAsync(server, NfsClientOptions.Default, ct);

    /// <summary>List exports advertised by mountd without mounting any export.</summary>
    public static async Task<IReadOnlyList<NfsExport>> ListExportsAsync(
        string server, NfsClientOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server)) throw new NfsException("NFS server is empty.");
        options ??= NfsClientOptions.Default;
        options.Validate();
        await using var client = new NfsV3Client(await RpcTransport.CreateAsync(server, options, ct), options);
        var port = await client._portmapClient.GetTcpPortAsync(
            NfsRpcConstants.ProgMount, NfsRpcConstants.VerMount, ct);
        PortmapClient.EnsureMapped(port, "mountd");
        return await client._mountClient.ListExportsAsync(port, ct);
    }

    /// <summary>Unmount the export and close the active NFS connection.</summary>
    public async Task UnmountAsync(CancellationToken ct)
    {
        if (_unmounted) return;
        _unmounted = true;
        _logger?.LogInformation("Unmounting NFS export {Export}", _exportPath);
        try
        {
            if (_mountPort > 0 && !string.IsNullOrWhiteSpace(_exportPath))
                await _mountClient.UnmountAsync(_mountPort, _exportPath, ct);
        }
        finally
        {
            await _rpcClient.CloseActiveConnectionAsync();
        }
    }

    /// <summary>LOOKUP in a directory handle.</summary>
    public Task<NfsLookup> LookupAsync(byte[] dirFh, string name, CancellationToken ct) =>
        _protocolClient.LookupAsync(dirFh, name, ct);
    /// <summary>LOOKUP an export-relative path.</summary>
    public Task<NfsLookup> LookupPathAsync(string path, CancellationToken ct) => _pathResolver.ResolveAsync(path, ct);
    /// <summary>GETATTR for a file handle.</summary>
    public Task<NfsFattr> GetAttributesAsync(byte[] fileHandle, CancellationToken ct) =>
        _protocolClient.GetAttributesAsync(fileHandle, ct);
    /// <summary>GETATTR for an export-relative path.</summary>
    public async Task<NfsFattr> GetAttributesAsync(string path, CancellationToken ct)
    {
        var item = await LookupPathAsync(path, ct);
        return item.Attr ?? await GetAttributesAsync(item.Handle, ct);
    }

    /// <summary>Return true when an export-relative path exists.</summary>
    public async Task<bool> FileExistsAsync(string path, CancellationToken ct)
    {
        try { await LookupPathAsync(path, ct); return true; }
        catch (NfsException ex) when (ex.IsNotFound) { return false; }
    }

    /// <summary>Return true when an export-relative path exists and is a directory.</summary>
    public async Task<bool> IsDirectoryAsync(string path, CancellationToken ct)
    {
        try { return (await GetAttributesAsync(path, ct)).Type == NfsType.Dir; }
        catch (NfsException ex) when (ex.IsNotFound) { return false; }
    }

    /// <summary>FSSTAT for a file handle — returns storage capacity and availability.</summary>
    public Task<NfsFileSystemStat> GetFileSystemStatAsync(byte[] fileHandle, CancellationToken ct) =>
        _protocolClient.GetFileSystemStatAsync(fileHandle, ct);
    /// <summary>FSSTAT for an export-relative path.</summary>
    public async Task<NfsFileSystemStat> GetFileSystemStatAsync(string path, CancellationToken ct) =>
        await GetFileSystemStatAsync((await LookupPathAsync(path, ct)).Handle, ct);
    /// <summary>FSINFO for a file handle — returns server transfer preferences and feature flags.</summary>
    public Task<NfsFileSystemInfo> GetFileSystemInfoAsync(byte[] fileHandle, CancellationToken ct) =>
        _protocolClient.GetFileSystemInfoAsync(fileHandle, ct);
    /// <summary>FSINFO for an export-relative path.</summary>
    public async Task<NfsFileSystemInfo> GetFileSystemInfoAsync(string path, CancellationToken ct) =>
        await GetFileSystemInfoAsync((await LookupPathAsync(path, ct)).Handle, ct);
    /// <summary>PATHCONF for a file handle — returns POSIX path constraints.</summary>
    public Task<NfsPathConf> GetPathConfAsync(byte[] fileHandle, CancellationToken ct) =>
        _protocolClient.GetPathConfAsync(fileHandle, ct);
    /// <summary>PATHCONF for an export-relative path.</summary>
    public async Task<NfsPathConf> GetPathConfAsync(string path, CancellationToken ct) =>
        await GetPathConfAsync((await LookupPathAsync(path, ct)).Handle, ct);
    /// <summary>ACCESS check on a file handle. Returns the granted access mask.</summary>
    public Task<NfsAccessMode> AccessAsync(byte[] fileHandle, NfsAccessMode desired, CancellationToken ct) =>
        _protocolClient.AccessAsync(fileHandle, desired, ct);
    /// <summary>ACCESS check on an export-relative path.</summary>
    public async Task<NfsAccessMode> AccessAsync(string path, NfsAccessMode desired, CancellationToken ct) =>
        await AccessAsync((await LookupPathAsync(path, ct)).Handle, desired, ct);
    /// <summary>READLINK — read the target of a symbolic link.</summary>
    public Task<string> ReadLinkAsync(byte[] symlinkHandle, CancellationToken ct) =>
        _protocolClient.ReadLinkAsync(symlinkHandle, ct);
    /// <summary>READLINK — read the target of an export-relative symbolic link path.</summary>
    public async Task<string> ReadLinkAsync(string path, CancellationToken ct) =>
        await ReadLinkAsync((await LookupPathAsync(path, ct)).Handle, ct);

    /// <summary>Resolve an export-relative path to a file handle (NFSv3 has no explicit OPEN).</summary>
    public async Task<NfsLookup> OpenFileAsync(string path, CancellationToken ct)
    {
        var item = await LookupPathAsync(path, ct);
        if (item.Attr?.Type == NfsType.Dir) throw new NfsException($"Path is a directory: {path}", NfsV3Status.IsDir);
        return item;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await UnmountAsync(source.Token);
        }
        catch { await _rpcClient.CloseActiveConnectionAsync(); }
        finally { await _rpcClient.DisposeAsync(); }
    }

    internal ValueTask DisposeActiveNfsConnectionForTestingAsync() =>
        _rpcClient.DisposeActiveConnectionForTestingAsync();
    internal static bool IsTransient(Exception ex) => NfsRetryPolicy.IsTransient(ex);
    internal static bool CanRetryTransient(uint program, uint version, uint procedure) =>
        NfsRetryPolicy.CanRetry(program, version, procedure);

    private static RpcReply DecodeRpcReplyWithContext(
        byte[] reply, uint xid, uint program, uint version, uint procedure) =>
        RpcClient.DecodeReplyWithContext(reply, xid, program, version, procedure);
    private static void EnsureDirectoryReadProgress(
        ulong requestCookie, ulong responseCookie, int entryCount, bool eof, string procedure) =>
        NfsV3ProtocolClient.EnsureDirectoryReadProgress(requestCookie, responseCookie, entryCount, eof, procedure);
}
