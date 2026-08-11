using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>
/// Managed NFSv3 client covering the core file and directory operations used by
/// backup browsing, restore tooling, and lightweight storage automation.
/// </summary>
public sealed class NfsV3Client : IAsyncDisposable
{
    private const uint ProgPortmap = 100000;
    private const uint VerPortmap = 2;
    private const uint ProgMount = 100005;
    private const uint VerMount = 3;
    private const uint ProgNfs = 100003;
    private const uint VerNfs = 3;

    private const uint PmapGetPort = 3;
    private const uint MountMnt = 1;
    private const uint MountUmnt = 3;
    private const uint MountExport = 5;
    private const uint NfsGetAttr = 1;
    private const uint NfsSetAttr = 2;
    private const uint NfsLookup = 3;
    private const uint NfsAccess = 4;
    private const uint NfsReadlink = 5;
    private const uint NfsRead = 6;
    private const uint NfsWrite = 7;
    private const uint NfsCreate = 8;
    private const uint NfsMkdir = 9;
    private const uint NfsSymlink = 10;
    private const uint NfsMknod = 11;
    private const uint NfsRemove = 12;
    private const uint NfsRmdir = 13;
    private const uint NfsRename = 14;
    private const uint NfsLink = 15;
    private const uint NfsReadDir = 16;
    private const uint NfsReadDirPlus = 17;
    private const uint NfsFsstat = 18;
    private const uint NfsFsinfo = 19;
    private const uint NfsPathconf = 20;
    private const uint NfsCommit = 21;

    private const uint IpprotoTcp = 6;
    private const int MaxRpcRecordLength = 64 * 1024 * 1024;
    private const NfsAccessMode ValidAccessMask =
        NfsAccessMode.Read |
        NfsAccessMode.Lookup |
        NfsAccessMode.Modify |
        NfsAccessMode.Extend |
        NfsAccessMode.Delete |
        NfsAccessMode.Execute;

    private readonly IPAddress _ip;
    private readonly NfsClientOptions _options;
    private readonly byte[] _credBody;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<byte[], DirCacheEntry> _dirCache = new(ByteArrayComparer.Instance);

    private Conn? _nfs;
    private byte[] _rootFh = Array.Empty<byte>();
    private string _exportPath = "";
    private int _mountPort;
    private int _nfsPort;
    private uint _xid;
    private bool _unmounted;
    private RpcSecGssContext? _gssContext;

    private NfsV3Client(IPAddress ip, NfsClientOptions options)
    {
        _ip = ip;
        _options = options;
        _credBody = BuildAuthSysBody(options);
        _logger = options.Logger;
    }

    /// <summary>File handle for the mounted export root.</summary>
    public byte[] RootHandle => _rootFh;

    /// <summary>Resolve a server, mount an export, and open the NFSv3 connection.</summary>
    public static Task<NfsV3Client> ConnectAsync(string server, string exportPath, CancellationToken ct) =>
        ConnectAsync(server, exportPath, NfsClientOptions.Default, ct);

    /// <summary>Resolve a server, mount an export, and open the NFSv3 connection.</summary>
    public static async Task<NfsV3Client> ConnectAsync(
        string server,
        string exportPath,
        NfsClientOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new NfsException("NFS server is empty.");
        if (string.IsNullOrWhiteSpace(exportPath))
            throw new NfsException("NFS export path is empty.");

        options ??= NfsClientOptions.Default;
        options.Validate();

        var ip = await ResolveAddressAsync(server, ct);
        var client = new NfsV3Client(ip, options) { _exportPath = exportPath };

        var mountPort = await client.GetPortAsync(ProgMount, VerMount, ct);
        var nfsPort = await client.GetPortAsync(ProgNfs, VerNfs, ct);

        EnsureMappedPort(mountPort, "mountd");
        EnsureMappedPort(nfsPort, "NFS");
        client._mountPort = mountPort;

        client._rootFh = await client.MountAsync(exportPath, ct);

        client._nfs = await client.OpenAsync(nfsPort, ct);
        client._nfsPort = nfsPort;

        if (options.GssMechanism is not null)
            await client.EstablishGssContextAsync(server, ct);

        client._logger?.LogInformation("NFS mounted {Export} on {Server} (mountd={MountPort}, nfsd={NfsPort})", exportPath, server, mountPort, nfsPort);
        return client;
    }

    /// <summary>List exports advertised by mountd without mounting any export.</summary>
    public static async Task<IReadOnlyList<NfsExport>> ListExportsAsync(
        string server,
        CancellationToken ct) =>
        await ListExportsAsync(server, NfsClientOptions.Default, ct);

    /// <summary>List exports advertised by mountd without mounting any export.</summary>
    public static async Task<IReadOnlyList<NfsExport>> ListExportsAsync(
        string server,
        NfsClientOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new NfsException("NFS server is empty.");

        options ??= NfsClientOptions.Default;
        options.Validate();

        var ip = await ResolveAddressAsync(server, ct);
        await using var client = new NfsV3Client(ip, options);

        var mountPort = await client.GetPortAsync(ProgMount, VerMount, ct);

        EnsureMappedPort(mountPort, "mountd");

        client._mountPort = mountPort;
        var reader = await client.CallWithOwnedConnectionAsync(
            mountPort,
            ProgMount,
            VerMount,
            MountExport,
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

    /// <summary>Unmount the export from mountd's perspective and close the NFS connection.</summary>
    public async Task UnmountAsync(CancellationToken ct)
    {
        if (_unmounted)
            return;

        _unmounted = true;
        _logger?.LogInformation("Unmounting NFS export {Export}", _exportPath);
        try
        {
            if (_mountPort > 0 && !string.IsNullOrWhiteSpace(_exportPath))
            {
                await using var mount = await OpenAsync(_mountPort, ct);
                var writer = new XdrWriter();
                writer.Str(_exportPath);
                await CallAsync(mount, ProgMount, VerMount, MountUmnt, writer.ToArray(), ct);
            }
        }
        finally
        {
            if (_nfs is not null)
            {
                await _nfs.DisposeAsync();
                _nfs = null;
            }
        }
    }

    /// <summary>LOOKUP in a directory handle.</summary>
    public async Task<NfsLookup> LookupAsync(byte[] dirFh, string name, CancellationToken ct)
    {
        ValidateHandle(dirFh);
        ValidateName(name);

        var writer = new XdrWriter();
        writer.Opaque(dirFh);
        writer.Str(name);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsLookup, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, $"LOOKUP \"{name}\" failed");

        var handle = reader.Opaque();
        var attr = ReadPostOpAttr(reader);
        ReadPostOpAttr(reader); // parent directory attributes
        return new NfsLookup(handle, attr);
    }

    /// <summary>LOOKUP an export-relative path.</summary>
    public async Task<NfsLookup> LookupPathAsync(string path, CancellationToken ct)
    {
        var handle = _rootFh;
        NfsLookup? current = null;
        foreach (var part in SplitPath(path))
        {
            current = await LookupAsync(handle, part, ct);
            handle = current.Handle;
        }

        return current ?? new NfsLookup(_rootFh, await GetAttributesAsync(_rootFh, ct));
    }

    /// <summary>GETATTR for a file handle.</summary>
    public async Task<NfsFattr> GetAttributesAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);

        var writer = new XdrWriter();
        writer.Opaque(fileHandle);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsGetAttr, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "GETATTR failed");
        return ReadFattr3(reader);
    }

    /// <summary>GETATTR for an export-relative path.</summary>
    public async Task<NfsFattr> GetAttributesAsync(string path, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return lookup.Attr ?? await GetAttributesAsync(lookup.Handle, ct);
    }

    /// <summary>Return true when an export-relative path exists.</summary>
    public async Task<bool> FileExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            await LookupPathAsync(path, ct);
            return true;
        }
        catch (NfsException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>Return true when an export-relative path exists and is a directory.</summary>
    public async Task<bool> IsDirectoryAsync(string path, CancellationToken ct)
    {
        try
        {
            var attr = await GetAttributesAsync(path, ct);
            return attr.Type == NfsType.Dir;
        }
        catch (NfsException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>FSSTAT for a file handle — returns storage capacity and availability.</summary>
    public async Task<NfsFileSystemStat> GetFileSystemStatAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);

        var writer = new XdrWriter();
        writer.Opaque(fileHandle);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsFsstat, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "FSSTAT failed");
        ReadPostOpAttr(reader);

        var totalBytes = reader.ULong();
        var freeBytes = reader.ULong();
        var availableBytes = reader.ULong();
        var totalFiles = reader.ULong();
        var freeFiles = reader.ULong();
        var availableFiles = reader.ULong();
        var invarSec = reader.UInt();

        return new NfsFileSystemStat(totalBytes, freeBytes, availableBytes, totalFiles, freeFiles, availableFiles, TimeSpan.FromSeconds(invarSec));
    }

    /// <summary>FSSTAT for an export-relative path.</summary>
    public async Task<NfsFileSystemStat> GetFileSystemStatAsync(string path, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return await GetFileSystemStatAsync(lookup.Handle, ct);
    }

    /// <summary>FSINFO for a file handle — returns server transfer preferences and feature flags.</summary>
    public async Task<NfsFileSystemInfo> GetFileSystemInfoAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);

        var writer = new XdrWriter();
        writer.Opaque(fileHandle);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsFsinfo, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "FSINFO failed");
        ReadPostOpAttr(reader);

        var rtmax = reader.UInt();
        var rtpref = reader.UInt();
        var rtmult = reader.UInt();
        var wtmax = reader.UInt();
        var wtpref = reader.UInt();
        var wtmult = reader.UInt();
        var dtpref = reader.UInt();
        var maxFileSize = reader.ULong();
        var timeDeltaSec = reader.UInt();
        var timeDeltaNsec = reader.UInt();
        var properties = reader.UInt();

        return new NfsFileSystemInfo
        {
            MaxReadSize = rtmax,
            PreferredReadSize = rtpref,
            ReadMultipleSize = rtmult,
            MaxWriteSize = wtmax,
            PreferredWriteSize = wtpref,
            WriteMultipleSize = wtmult,
            PreferredReaddirSize = dtpref,
            MaxFileSize = maxFileSize,
            TimeDelta = TimeSpan.FromSeconds(timeDeltaSec).Add(TimeSpan.FromTicks(timeDeltaNsec / 100)),
            Properties = properties
        };
    }

    /// <summary>FSINFO for an export-relative path.</summary>
    public async Task<NfsFileSystemInfo> GetFileSystemInfoAsync(string path, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return await GetFileSystemInfoAsync(lookup.Handle, ct);
    }

    /// <summary>PATHCONF for a file handle — returns POSIX path constraints.</summary>
    public async Task<NfsPathConf> GetPathConfAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);

        var writer = new XdrWriter();
        writer.Opaque(fileHandle);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsPathconf, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "PATHCONF failed");
        ReadPostOpAttr(reader);

        var linkMax = reader.UInt();
        var nameMax = reader.UInt();
        var noTrunc = reader.Bool();
        var chownRestricted = reader.Bool();
        var caseInsensitive = reader.Bool();
        var casePreserving = reader.Bool();

        return new NfsPathConf
        {
            LinkMax = linkMax,
            NameMax = nameMax,
            NoTrunc = noTrunc,
            ChownRestricted = chownRestricted,
            CaseInsensitive = caseInsensitive,
            CasePreserving = casePreserving
        };
    }

    /// <summary>PATHCONF for an export-relative path.</summary>
    public async Task<NfsPathConf> GetPathConfAsync(string path, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return await GetPathConfAsync(lookup.Handle, ct);
    }

    /// <summary>ACCESS check on a file handle. Returns the granted access mask.</summary>
    public async Task<NfsAccessMode> AccessAsync(byte[] fileHandle, NfsAccessMode desired, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        ValidateAccessMode(desired);

        var writer = new XdrWriter();
        writer.Opaque(fileHandle);
        writer.UInt((uint)desired);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsAccess, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "ACCESS failed");
        ReadPostOpAttr(reader);
        var granted = (NfsAccessMode)reader.UInt();
        if ((granted & ~desired) != NfsAccessMode.None)
        {
            throw new NfsException(
                $"ACCESS returned grant 0x{(uint)granted:X} outside requested mask 0x{(uint)desired:X}.");
        }

        return granted;
    }

    /// <summary>ACCESS check on an export-relative path.</summary>
    public async Task<NfsAccessMode> AccessAsync(string path, NfsAccessMode desired, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return await AccessAsync(lookup.Handle, desired, ct);
    }

    /// <summary>READLINK — read the target of a symbolic link.</summary>
    public async Task<string> ReadLinkAsync(byte[] symlinkHandle, CancellationToken ct)
    {
        ValidateHandle(symlinkHandle);

        var writer = new XdrWriter();
        writer.Opaque(symlinkHandle);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsReadlink, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "READLINK failed");
        ReadPostOpAttr(reader);
        return reader.Str();
    }

    /// <summary>READLINK — read the target of an export-relative symbolic link path.</summary>
    public async Task<string> ReadLinkAsync(string path, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return await ReadLinkAsync(lookup.Handle, ct);
    }

    /// <summary>Resolve an export-relative path to a file handle (NFSv3 has no explicit OPEN).</summary>
    public async Task<NfsLookup> OpenFileAsync(string path, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        if (lookup.Attr?.Type == NfsType.Dir)
            throw new NfsException($"Path is a directory: {path}", NfsV3Status.IsDir);
        return lookup;
    }

    /// <summary>Create a file and return its handle.</summary>
    public async Task<NfsLookup> CreateAndOpenFileAsync(string path, NfsSetAttributes? attributes, CancellationToken ct)
    {
        var (parent, name) = await ResolveParentAsync(path, ct);
        return await CreateFileAsync(parent, name, attributes ?? NfsSetAttributes.FileDefault, ct);
    }

    /// <summary>COMMIT — flush cached data to stable storage for a file handle.</summary>
    public async Task CommitAsync(byte[] fileFh, ulong offset, uint count, CancellationToken ct)
    {
        await CommitWithResultAsync(fileFh, offset, count, ct);
    }

    /// <summary>COMMIT — flush cached data to stable storage and return the server write verifier.</summary>
    public async Task<NfsCommitResult> CommitWithResultAsync(byte[] fileFh, ulong offset, uint count, CancellationToken ct)
    {
        ValidateHandle(fileFh);

        var writer = new XdrWriter();
        writer.Opaque(fileFh);
        writer.ULong(offset);
        writer.UInt(count);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsCommit, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, "COMMIT failed");
        ReadWccData(reader);
        var writeVerifier = reader.FixedBytes(8);
        return new NfsCommitResult(writeVerifier);
    }

    /// <summary>COMMIT — flush cached data to stable storage for an export-relative file path.</summary>
    public async Task CommitAsync(string path, ulong offset, uint count, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        await CommitAsync(lookup.Handle, offset, count, ct);
    }

    /// <summary>COMMIT — flush cached data to stable storage for an export-relative path and return the server write verifier.</summary>
    public async Task<NfsCommitResult> CommitWithResultAsync(string path, ulong offset, uint count, CancellationToken ct)
    {
        var lookup = await LookupPathAsync(path, ct);
        return await CommitWithResultAsync(lookup.Handle, offset, count, ct);
    }

    /// <summary>SYMLINK — create a symbolic link in a directory handle.</summary>
    public async Task<NfsLookup> CreateSymLinkAsync(
        byte[] dirFh,
        string linkName,
        string targetPath,
        NfsSetAttributes? attributes,
        CancellationToken ct)
    {
        ValidateHandle(dirFh);
        ValidateName(linkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var writer = new XdrWriter();
        writer.Opaque(dirFh);
        writer.Str(linkName);
        writer.Str(targetPath);
        WriteSattr3(writer, attributes ?? NfsSetAttributes.FileDefault);

        var reader = await CallAsync(RequireNfs(), ProgNfs, VerNfs, NfsSymlink, writer.ToArray(), ct);
        var status = reader.UInt();
        EnsureOk(status, $"SYMLINK \"{linkName}\" failed");
        InvalidateDirCache(dirFh);
        return ReadDiropOk(reader);
    }

    /// <summary>SYMLINK — create a symbolic link at an export-relative path.</summary>
    public async Task<NfsLookup> CreateSymLinkAsync(string linkPath, string targetPath, CancellationToken ct)
    {
        var (parent, name) = await ResolveParentAsync(linkPath, ct);
        return await CreateSymLinkAsync(parent, name, targetPath, null, ct);
    }

    /// <summary>LINK — c…8160 tokens truncated…r.UInt(vers);
            writer.UInt(proc);

            if (_gssContext is not null && _gssContext.Mechanism.IsEstablished)
            {
                // RPCSEC_GSS credential
                writer.UInt((uint)RpcSecGssFlavor.Gss);
                EncodeGssCredential(writer, proc, args);

                // RPCSEC_GSS verifier
                writer.UInt((uint)RpcSecGssFlavor.Gss);
                EncodeGssVerifier(writer, xid, args);
            }
            else
            {
                // AUTH_SYS
                writer.UInt(1); // AUTH_SYS
                writer.Opaque(_credBody);
                writer.UInt(0); // AUTH_NONE verifier
                writer.UInt(0);
            }

            writer.Raw(args);

            await SendRecordAsync(conn.Stream, writer.ToArray(), token);
            var reply = await RpcRecordStream.ReceiveAsync(conn.Stream, token);

            var rpcReply = DecodeRpcReplyWithContext(reply, xid, prog, vers, proc);
            var reader = rpcReply.Body;

            // Verify GSS response verifier if applicable
            if (_gssContext is not null && rpcReply.VerifierFlavor == (uint)RpcSecGssFlavor.Gss)
            {
                var replyVerifier = rpcReply.Verifier;
                // In a full implementation, we'd verify the MIC here
            }

            return reader;
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private static void EnsureMappedPort(int port, string service)
    {
        if (port == 0)
            throw new NfsException($"{service} service is not registered in portmap for TCP.");
    }

    private static RpcReply DecodeRpcReplyWithContext(
        byte[] reply,
        uint xid,
        uint prog,
        uint vers,
        uint proc)
    {
        try
        {
            return RpcReplyParser.Decode(reply, xid);
        }
        catch (NfsException ex)
        {
            throw new NfsException($"RPC call failed (prog={prog}, vers={vers}, proc={proc}): {ex.Message}", ex);
        }
    }

    private void EncodeGssCredential(XdrWriter writer, uint proc, byte[] args)
    {
        if (_gssContext is null) return;

        var ctxHandle = _gssContext.ContextHandle;
        var seqNum = _gssContext.Mechanism.NextSeqNum++;
        var service = _gssContext.Service;

        // RPCSEC_GSS credential body: handle(4) + seq_num(4) + service(4) + handle_length + handle
        var credBody = new XdrWriter();
        credBody.UInt((uint)ctxHandle.Length);
        credBody.Opaque(ctxHandle);
        credBody.UInt(seqNum);
        credBody.UInt((uint)service);

        writer.Opaque(credBody.ToArray());
    }

    private void EncodeGssVerifier(XdrWriter writer, uint xid, byte[] args)
    {
        if (_gssContext is null || !_gssContext.Mechanism.IsEstablished)
        {
            writer.UInt(0);
            return;
        }

        // Compute MIC over the RPC header + args
        var micData = _gssContext.Mechanism.GetMic(args);
        writer.Opaque(micData);
    }

    private async Task EstablishGssContextAsync(string server, CancellationToken ct)
    {
        var mechanism = _options.GssMechanism!;
        var targetName = _options.GssTargetName ?? $"nfs/{server}";

        // Step 1: Initiate context
        var token = await mechanism.InitiateContextAsync(targetName, _options.GssCredentials, ct);

        // Send RPCSEC_GSS_CREATE request
        var createArgs = new XdrWriter();
        createArgs.UInt((uint)RpcSecGssProc.Create);
        createArgs.UInt((uint)token.Length);
        createArgs.Opaque(token);
        createArgs.UInt((uint)_options.GssService);
        createArgs.UInt(0); // seq window size hint

        var conn = RequireNfs();
        var reader = await CallRawAsync(conn, ProgNfs, 3, 0, createArgs.ToArray(), ct);

        // Parse CREATE response
        var status = reader.UInt();
        if (status != 0)
            throw new NfsException($"RPCSEC_GSS_CREATE failed (stat={status}).");

        var contextHandle = reader.Opaque();
        var seqWindowSize = reader.UInt();
        var seqWindow = reader.FixedBytes(8);

        _gssContext = new RpcSecGssContext
        {
            ContextHandle = contextHandle,
            SeqWindowSize = seqWindowSize,
            SeqWindow = seqWindow,
            Service = _options.GssService,
            Mechanism = mechanism,
        };

        mechanism.NegotiatedService = _options.GssService;
        _logger?.LogInformation("RPCSEC_GSS context established (target={Target}, service={Service})", targetName, _options.GssService);
    }

    private async Task<XdrReader> CallRawAsync(Conn conn, uint prog, uint vers, uint proc, byte[] args, CancellationToken ct)
    {
        var xid = unchecked(++_xid);
        var writer = new XdrWriter();
        writer.UInt(xid);
        writer.UInt(0);
        writer.UInt(2);
        writer.UInt(prog);
        writer.UInt(vers);
        writer.UInt(proc);
        writer.UInt(1); // AUTH_SYS
        writer.Opaque(_credBody);
        writer.UInt(0);
        writer.UInt(0);
        writer.Raw(args);

        await SendRecordAsync(conn.Stream, writer.ToArray(), ct);
        var reply = await RpcRecordStream.ReceiveAsync(conn.Stream, ct);

        return DecodeRpcReplyWithContext(reply, xid, prog, vers, proc).Body;
    }

    internal static bool IsTransient(Exception ex) =>
        ex is SocketException or IOException or ObjectDisposedException ||
        ex is NfsException { InnerException: Exception inner } && IsTransient(inner);

    internal static bool CanRetryTransient(uint prog, uint vers, uint proc) =>
        (prog, vers, proc) switch
        {
            (ProgPortmap, VerPortmap, PmapGetPort) => true,
            (ProgMount, VerMount, MountMnt or MountExport) => true,
            (ProgNfs, VerNfs, NfsGetAttr or NfsLookup or NfsAccess or NfsReadlink or NfsRead or
                NfsReadDir or NfsReadDirPlus or NfsFsstat or NfsFsinfo or NfsPathconf or NfsCommit) => true,
            _ => false
        };

    private async Task<Conn?> ReconnectAsync(CancellationToken ct)
    {
        if (_nfsPort <= 0 || _unmounted)
            return null;

        _logger?.LogInformation("Reconnecting to NFS server (port={Port})", _nfsPort);
        if (_nfs is not null)
        {
            try { await _nfs.DisposeAsync(); } catch { }
            _nfs = null;
        }

        using var timeoutCts = CreateCallTimeout(ct, out var token);
        _nfs = await OpenAsync(_nfsPort, token);
        _logger?.LogInformation("Reconnected to NFS server");
        return _nfs;
    }

    private async Task RefreshTimedOutNfsConnectionAsync(Conn conn, CancellationToken ct)
    {
        if (!ReferenceEquals(conn, _nfs) || _nfsPort <= 0 || _unmounted)
            return;

        try
        {
            _logger?.LogWarning("Refreshing NFS connection after RPC command timeout");
            await ReconnectAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning("Timed out while refreshing NFS connection after RPC command timeout");
        }
        catch (Exception reconnectEx)
        {
            _logger?.LogWarning(reconnectEx, "Failed to refresh NFS connection after RPC command timeout");
        }
    }

    private CancellationTokenSource? CreateCallTimeout(CancellationToken outer, out CancellationToken token)
    {
        if (_options.CommandTimeout <= TimeSpan.Zero)
        {
            token = outer;
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(_options.CommandTimeout);
        token = cts.Token;
        return cts;
    }

    private static async Task SendRecordAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x8000_0000u | (uint)message.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(message, ct);
        await stream.FlushAsync(ct);
    }

    private static NfsLookup ReadDiropOk(XdrReader reader)
    {
        var hasHandle = reader.Bool();
        var handle = hasHandle ? reader.Opaque() : Array.Empty<byte>();
        var attr = ReadPostOpAttr(reader);
        ReadWccData(reader);
        return new NfsLookup(handle, attr);
    }

    private static NfsFattr? ReadPostOpAttr(XdrReader reader) =>
        reader.Bool() ? ReadFattr3(reader) : null;

    private static NfsFattr ReadFattr3(XdrReader reader)
    {
        var type = (NfsType)reader.UInt();
        var mode = reader.UInt();
        var nlink = reader.UInt();
        var uid = reader.UInt();
        var gid = reader.UInt();
        var size = reader.ULong();
        var used = reader.ULong();
        reader.UInt();
        reader.UInt();
        var fsid = reader.ULong();
        var fileId = reader.ULong();
        var atime = ReadNfsTimestamp(reader);
        var mtime = ReadNfsTimestamp(reader);
        var ctime = ReadNfsTimestamp(reader);

        if (size > long.MaxValue)
            throw new NfsException($"NFSv3 file size {size} exceeds the supported Int64 range.");

        return new NfsFattr(type, (long)size, mtime?.ToDateTimeUtc())
        {
            Mode = mode,
            LinkCount = nlink,
            Uid = uid,
            Gid = gid,
            Used = used,
            FileSystemId = fsid,
            FileId = fileId,
            Atime = atime?.ToDateTimeUtc(),
            Ctime = ctime?.ToDateTimeUtc(),
            CtimeTimestamp = ctime
        };
    }

    private static void ReadWccData(XdrReader reader)
    {
        if (reader.Bool())
        {
            reader.ULong(); // size
            ReadNfsTimestamp(reader);
            ReadNfsTimestamp(reader);
        }

        ReadPostOpAttr(reader);
    }

    private static NfsTimestamp? ReadNfsTimestamp(XdrReader reader)
    {
        var seconds = reader.UInt();
        var nanos = reader.UInt();
        if (seconds == 0 && nanos == 0)
            return null;

        return new NfsTimestamp(seconds, nanos);
    }

    private static void WriteSattr3(XdrWriter writer, NfsSetAttributes attributes)
    {
        WriteOptionalUInt(writer, attributes.Mode);
        WriteOptionalUInt(writer, attributes.Uid);
        WriteOptionalUInt(writer, attributes.Gid);
        WriteOptionalULong(writer, attributes.Size);
        WriteOptionalTime(writer, attributes.Atime);
        WriteOptionalTime(writer, attributes.Mtime);
    }

    private static void WriteSattrGuard3(XdrWriter writer, NfsTimestamp? guardCtime)
    {
        writer.Bool(guardCtime.HasValue);
        if (guardCtime.HasValue)
            WriteNfsTimestamp(writer, guardCtime.Value);
    }

    private static void WriteOptionalUInt(XdrWriter writer, uint? value)
    {
        writer.Bool(value.HasValue);
        if (value.HasValue)
            writer.UInt(value.Value);
    }

    private static void WriteOptionalULong(XdrWriter writer, ulong? value)
    {
        writer.Bool(value.HasValue);
        if (value.HasValue)
            writer.ULong(value.Value);
    }

    private static void WriteOptionalTime(XdrWriter writer, DateTime? value)
    {
        if (!value.HasValue)
        {
            writer.UInt(0); // DONT_CHANGE
            return;
        }

        var utc = value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : value.Value.ToUniversalTime();
        writer.UInt(2); // SET_TO_CLIENT_TIME
        WriteNfsTimestamp(writer, NfsTimestamp.FromDateTime(utc));
    }

    private static void WriteNfsTimestamp(XdrWriter writer, NfsTimestamp value)
    {
        writer.UInt(value.Seconds);
        writer.UInt(value.Nanoseconds);
    }

    private async Task<Conn> OpenAsync(int port, CancellationToken ct)
    {
        var socket = await ConnectAsync(_ip, port, _options.UsePrivilegedSourcePort, ct);
        ApplySocketOptions(socket, _options);
        return new Conn(socket);
    }

    private static async Task<Socket> ConnectAsync(
        IPAddress ip,
        int port,
        bool usePrivilegedSourcePort,
        CancellationToken ct)
    {
        if (usePrivilegedSourcePort)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var sourcePort = 1023 - Random.Shared.Next(0, 512);
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, sourcePort));
                    await socket.ConnectAsync(ip, port, ct);
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

        var fallback = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await fallback.ConnectAsync(ip, port, ct);
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

        if (options.TcpKeepAlive)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var interval = (int)options.KeepAliveInterval.TotalSeconds;
            if (interval > 0)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, interval);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, interval);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                }
                catch (SocketException)
                {
                    // Not all platforms support all TCP keepalive options; best-effort.
                }
            }
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

    private static byte[] BuildAuthSysBody(NfsClientOptions options)
    {
        string machine;
        try
        {
            machine = Dns.GetHostName();
        }
        catch
        {
            machine = "nfssharp";
        }

        return RpcAuthSys.Encode(0, machine, options.UserId, options.GroupId, options.AuxiliaryGroups ?? Array.Empty<uint>());
    }

    private static void EnsureOk(uint status, string message)
    {
        if (status != NfsV3Status.Ok)
            throw new NfsException($"{message} (nfsstat3={NfsV3Status.Describe(status)}).", status);
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "/")
            yield break;

        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
                throw new NfsException("Parent path traversal is not allowed.");

            ValidateName(part);
            yield return part;
        }
    }

    private static void ValidateHandle(byte[] handle)
    {
        if (handle is null || handle.Length == 0)
            throw new NfsException("NFS file handle is empty.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new NfsException("NFS path component is empty.");
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            throw new NfsException($"Invalid NFS path component: {name}");
        if (name.Length > 255)
            throw new NfsException($"NFS path component is too long: {name}");
    }

    private static void ValidateAccessMode(NfsAccessMode desired)
    {
        var invalid = desired & ~ValidAccessMask;
        if (invalid != NfsAccessMode.None)
            throw new NfsException($"Invalid ACCESS mask: 0x{(uint)desired:X}.");
    }

    private static void ValidateBufferRange(int bufferLength, int bufferOffset, int count)
    {
        if (bufferOffset < 0)
            throw new NfsException("Buffer offset cannot be negative.");
        if (count < 0)
            throw new NfsException("Count cannot be negative.");
        if (bufferOffset > bufferLength || count > bufferLength - bufferOffset)
            throw new NfsException("Buffer offset and count exceed the buffer length.");
    }

    private static void ValidateReadableStream(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
            throw new NfsException("Input stream must be readable.");
    }

    private static void ValidateWritableStream(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new NfsException("Output stream must be writable.");
    }

    private void InvalidateDirCache(byte[] dirHandle)
    {
        if (_options.EnableDirectoryCache)
            _dirCache.TryRemove(dirHandle, out _);
    }

    private void InvalidateDirCacheForMutation(byte[] handle)
    {
        if (!_options.EnableDirectoryCache || _dirCache.IsEmpty)
            return;

        _dirCache.TryRemove(handle, out _);
        if (_dirCache.IsEmpty)
            return;

        foreach (var cached in _dirCache)
        {
            if (cached.Value.Entries.Any(entry => entry.Handle is not null && entry.Handle.AsSpan().SequenceEqual(handle)))
                _dirCache.TryRemove(cached.Key, out _);
        }
    }

    private static List<NfsEntryPlus> CloneDirEntries(IEnumerable<NfsEntryPlus> entries) =>
        entries
            .Select(entry => entry with { Handle = entry.Handle?.ToArray() })
            .ToList();

    private sealed class Conn : IAsyncDisposable
    {
        private readonly Socket _socket;

        public Conn(Socket socket)
        {
            _socket = socket;
            Stream = new NetworkStream(socket, ownsSocket: false);
        }

        public NetworkStream Stream { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stream.DisposeAsync();
            }
            catch
            {
                // ignored
            }

            try
            {
                _socket.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }

    private sealed record DirCacheEntry(List<NfsEntryPlus> Entries, DateTime Expiry);

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
