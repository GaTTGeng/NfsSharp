using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>
/// NFSv4 client supporting COMPOUND operations for v4.0, v4.1, and v4.2.
/// </summary>
public sealed class NfsV4Client : IAsyncDisposable
{
    private const uint ProgPortmap = 100000;
    private const uint VerPortmap = 2;
    private const uint ProgNfs = 100003;
    private const uint IpprotoTcp = 6;
    private const int DefaultNfsPort = 2049;
    private const int MaxRpcRecordLength = 64 * 1024 * 1024;

    private readonly IPAddress _ip;
    private readonly NfsClientOptions _options;
    private readonly byte[] _credBody;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private readonly ILogger? _logger;
    private readonly uint _minorVersion;

    private Conn? _nfs;
    private int _nfsPort;
    private uint _xid;

    private byte[] _currentFh = Array.Empty<byte>();
    private byte[] _savedFh = Array.Empty<byte>();
    private ulong _clientId;
    private byte[] _clientVerifier = new byte[8];
    private NfsV4StateId? _currentStateId = NfsV4StateId.Anonymous;
    private ulong _sequenceId;

    private NfsV4Client(IPAddress ip, NfsClientOptions options, uint minorVersion)
    {
        _ip = ip;
        _options = options;
        _credBody = BuildAuthSysBody(options);
        _logger = options.Logger;
        _minorVersion = minorVersion;
        new Random().NextBytes(_clientVerifier);
    }

    /// <summary>Connect to an NFSv4 server.</summary>
    public static async Task<NfsV4Client> ConnectAsync(
        string server,
        uint minorVersion,
        NfsClientOptions? options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new NfsException("NFS server is empty.");
        if (minorVersion > 2)
            throw new NfsException($"Unsupported NFSv4 minor version: {minorVersion}.");

        options ??= NfsClientOptions.Default;
        options.Validate();

        var ip = await ResolveAddressAsync(server, ct);
        var client = new NfsV4Client(ip, options, minorVersion);

        var nfsPort = options.PortmapPort > 0
            ? await client.DiscoverNfsPortAsync(ip, options, ct)
            : DefaultNfsPort;

        client._nfsPort = nfsPort;
        client._nfs = await client.OpenAsync(nfsPort, ct);

        if (minorVersion >= 1)
            await client.EstablishSessionAsync(ct);
        else
            await client.SetClientIdAsync(ct);

        client._logger?.LogInformation("NFSv4.{Minor} connected to {Server}:{Port}", minorVersion, server, nfsPort);
        return client;
    }

    /// <summary>Connect to an NFSv4.0 server.</summary>
    public static Task<NfsV4Client> ConnectAsync(string server, CancellationToken ct) =>
        ConnectAsync(server, 0, null, ct);

    /// <summary>Execute a COMPOUND request.</summary>
    public async Task<NfsV4CompoundResponse> CompoundAsync(NfsV4CompoundRequest request, CancellationToken ct)
    {
        request.MinorVersion = _minorVersion;
        await _rpcLock.WaitAsync(ct);
        try
        {
            using var timeoutCts = CreateCallTimeout(ct, out var token);
            var xid = unchecked(++_xid);

            var writer = new XdrWriter();
            writer.UInt(xid);
            writer.UInt(0); // CALL
            writer.UInt(2); // RPC version
            writer.UInt(ProgNfs);
            writer.UInt(4); // NFSv4
            writer.UInt(0); // COMPOUND procedure
            writer.UInt(1); // AUTH_SYS
            writer.Opaque(_credBody);
            writer.UInt(0); // AUTH_NONE verifier
            writer.UInt(0);

            EncodeCompoundRequest(writer, request);

            var conn = RequireNfs();
            await SendRecordAsync(conn.Stream, writer.ToArray(), token);
            var reply = await RecvRecordAsync(conn.Stream, token);

            var reader = new XdrReader(reply);
            var rxid = reader.UInt();
            if (rxid != xid)
                throw new NfsException($"RPC xid mismatch. Expected {xid}, got {rxid}.");

            var messageType = reader.UInt();
            if (messageType != 1)
                throw new NfsException($"Unexpected RPC message type: {messageType}.");

            var replyStat = reader.UInt();
            if (replyStat != 0)
                throw new NfsException($"RPC message denied (reply_stat={replyStat}).");

            reader.UInt(); // verifier flavor
            reader.SkipOpaque();
            var acceptStat = reader.UInt();
            if (acceptStat != 0)
                throw new NfsException($"RPC call failed (accept_stat={acceptStat}).");

            return DecodeCompoundResponse(reader);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && _options.CommandTimeout > TimeSpan.Zero)
        {
            throw new NfsException($"RPC call timed out after {_options.CommandTimeout}.", ex);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    /// <summary>PUTROOTFH + LOOKUP path, then GETATTR.</summary>
    public async Task<NfsV4Fattr> GetAttributesAsync(string path, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeGetAttrOp(NfsV4Attr.Type, NfsV4Attr.Size, NfsV4Attr.Mode, NfsV4Attr.Owner,
            NfsV4Attr.OwnerGroup, NfsV4Attr.TimeAccess, NfsV4Attr.TimeModify, NfsV4Attr.Numinlinks));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "getattr", Operations = ops }, ct);
        EnsureCompoundOk(resp, "GETATTR");

        var attrResult = resp.Results[^1];
        return DecodeFattr(attrResult.Data!);
    }

    /// <summary>PUTROOTFH + LOOKUP path, then READ.</summary>
    public async Task<byte[]> ReadAsync(string path, ulong offset, uint count, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeReadOp(offset, count, _minorVersion >= 1 ? _currentStateId ?? NfsV4StateId.Anonymous : NfsV4StateId.Anonymous));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "read", Operations = ops }, ct);
        EnsureCompoundOk(resp, "READ");

        var readResult = resp.Results[^1];
        var eof = readResult.Data!.Bool();
        var data = readResult.Data.Opaque();
        return data;
    }

    /// <summary>PUTROOTFH + LOOKUP path, then WRITE.</summary>
    public async Task<int> WriteAsync(string path, ulong offset, ReadOnlyMemory<byte> data, NfsWriteStableHow stableHow, CancellationToken ct)
    {
        var stateId = _minorVersion >= 1 ? _currentStateId ?? NfsV4StateId.Anonymous : NfsV4StateId.Anonymous;
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeWriteOp(offset, data.ToArray(), stableHow, stateId));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "write", Operations = ops }, ct);
        EnsureCompoundOk(resp, "WRITE");

        var writeResult = resp.Results[^1];
        var count = writeResult.Data!.UInt();
        var committed = (NfsWriteStableHow)writeResult.Data.UInt();
        return (int)count;
    }

    /// <summary>PUTROOTFH + LOOKUP dir, then CREATE.</summary>
    public async Task CreateAsync(string dirPath, string name, NfsV4FType type, NfsSetAttributes? attributes, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(dirPath))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeCreateOp(name, type, attributes));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "create", Operations = ops }, ct);
        EnsureCompoundOk(resp, "CREATE");
    }

    /// <summary>PUTROOTFH + LOOKUP dir, then REMOVE.</summary>
    public async Task RemoveAsync(string dirPath, string name, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(dirPath))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeRemoveOp(name));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "remove", Operations = ops }, ct);
        EnsureCompoundOk(resp, "REMOVE");
    }

    /// <summary>PUTROOTFH + LOOKUP dir, then RENAME.</summary>
    public async Task RenameAsync(string fromDir, string fromName, string toDir, string toName, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(fromDir))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeOp(NfsV4Op.SaveFh));
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(toDir))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeRenameOp(fromName, toName));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "rename", Operations = ops }, ct);
        EnsureCompoundOk(resp, "RENAME");
    }

    /// <summary>PUTROOTFH + LOOKUP path, then READDIR.</summary>
    public async Task<List<NfsV4DirEntry>> ReadDirAsync(string path, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeReadDirOp(0));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "readdir", Operations = ops }, ct);
        EnsureCompoundOk(resp, "READDIR");

        var dirResult = resp.Results[^1];
        return DecodeReadDirEntries(dirResult.Data!);
    }

    /// <summary>OPEN + WRITE + COMMIT + CLOSE as a compound operation.</summary>
    public async Task<int> OpenWriteCloseAsync(string path, ulong offset, ReadOnlyMemory<byte> data, NfsWriteStableHow stableHow, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();

        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        var parts = SplitPath(path).ToArray();
        for (var i = 0; i < parts.Length - 1; i++)
            ops.Add(MakeLookupOp(parts[i]));

        var fileName = parts[^1];
        ops.Add(MakeOpenOp(fileName, NfsV4OpenShareAccess.Write, NfsV4OpenShareDeny.None));
        ops.Add(MakeOp(NfsV4Op.GetFh));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "open-write-close", Operations = ops }, ct);
        EnsureCompoundOk(resp, "OPEN");

        var openResult = resp.Results[ops.Count - 2];
        var stateId = NfsV4StateId.Decode(openResult.Data!);
        var fh = resp.Results[^1].Data!.Opaque();

        _currentFh = fh;

        var writeOps = new List<NfsV4Operation>();
        writeOps.Add(MakeOp(NfsV4Op.Putfh, w => w.Opaque(fh)));
        writeOps.Add(MakeWriteOp(offset, data.ToArray(), stableHow, stateId));
        writeOps.Add(MakeCommitOp(offset, (uint)data.Length));
        writeOps.Add(MakeCloseOp(stateId));

        var writeResp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "write-commit-close", Operations = writeOps }, ct);
        EnsureCompoundOk(writeResp, "WRITE/COMMIT/CLOSE");

        var writeResult = writeResp.Results[1];
        return (int)writeResult.Data!.UInt();
    }

    /// <summary>SECINFO — discover security flavors for a path.</summary>
    public async Task<List<uint>> SecInfoAsync(string path, CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeOp(NfsV4Op.SecInfo, w => { w.Str(path.Split('/').LastOrDefault() ?? ""); }));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "secinfo", Operations = ops }, ct);
        EnsureCompoundOk(resp, "SECINFO");

        var secResult = resp.Results[^1];
        var flavors = new List<uint>();
        var count = secResult.Data!.UInt();
        for (var i = 0; i < count; i++)
        {
            var flavor = secResult.Data.UInt();
            if (flavor == 6)
            {
                secResult.Data.UInt(); // gss oid count
                var oidCount = secResult.Data.UInt();
                for (var j = 0; j < oidCount; j++)
                    secResult.Data.SkipOpaque();
                secResult.Data.UInt(); // qop
                secResult.Data.UInt(); // service
            }
            flavors.Add(flavor);
        }
        return flavors;
    }

    // --- NFSv4.1 Session Management ---

    private async Task SetClientIdAsync(CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeSetClientIdOp());
        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "setclientid", Operations = ops }, ct);
        EnsureCompoundOk(resp, "SETCLIENTID");

        var result = resp.Results[0].Data!;
        _clientId = result.ULong();
        result.FixedBytes(8); // setclientid_confirm verifier

        var confirmOps = new List<NfsV4Operation>();
        confirmOps.Add(MakeOp(NfsV4Op.SetClientIdConfirm, w => { w.ULong(_clientId); w.FixedBytes(_clientVerifier); }));
        var confirmResp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "setclientid-confirm", Operations = confirmOps }, ct);
        EnsureCompoundOk(confirmResp, "SETCLIENTID_CONFIRM");
    }

    private async Task EstablishSessionAsync(CancellationToken ct)
    {
        var ops = new List<NfsV4Operation>();
        ops.Add(MakeExchangeIdOp());
        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "exchange_id", Operations = ops }, ct);
        EnsureCompoundOk(resp, "EXCHANGE_ID");

        var result = resp.Results[0].Data!;
        _clientId = result.ULong();
        _sequenceId = result.ULong();

        var createOps = new List<NfsV4Operation>();
        createOps.Add(MakeCreateSessionOp());
        var createResp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "create_session", Operations = createOps }, ct);
        EnsureCompoundOk(createResp, "CREATE_SESSION");

        _logger?.LogInformation("NFSv4.1 session established (clientId={ClientId}, seqId={SeqId})", _clientId, _sequenceId);
    }

    // --- NFSv4.2 Operations ---

    /// <summary>SEEK — find data or hole in a sparse file (v4.2).</summary>
    public async Task<(ulong Offset, bool IsHole)> SeekAsync(string path, ulong offset, bool seekHole, CancellationToken ct)
    {
        if (_minorVersion < 2)
            throw new NfsException("SEEK requires NFSv4.2+.");

        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeSeekOp(offset, seekHole));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "seek", Operations = ops }, ct);
        EnsureCompoundOk(resp, "SEEK");

        var result = resp.Results[^1].Data!;
        var eof = result.Bool();
        var foundOffset = result.ULong();
        return (foundOffset, eof);
    }

    /// <summary>ALLOCATE — allocate space for a sparse file (v4.2).</summary>
    public async Task AllocateAsync(string path, ulong offset, ulong length, CancellationToken ct)
    {
        if (_minorVersion < 2)
            throw new NfsException("ALLOCATE requires NFSv4.2+.");

        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeAllocateOp(offset, length));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "allocate", Operations = ops }, ct);
        EnsureCompoundOk(resp, "ALLOCATE");
    }

    /// <summary>DEALLOCATE — punch a hole in a sparse file (v4.2).</summary>
    public async Task DeallocateAsync(string path, ulong offset, ulong length, CancellationToken ct)
    {
        if (_minorVersion < 2)
            throw new NfsException("DEALLOCATE requires NFSv4.2+.");

        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(path))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeDeallocateOp(offset, length));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "deallocate", Operations = ops }, ct);
        EnsureCompoundOk(resp, "DEALLOCATE");
    }

    /// <summary>COPY — server-side file copy (v4.2).</summary>
    public async Task<ulong> CopyAsync(string srcPath, ulong srcOffset, string dstPath, ulong dstOffset, ulong count, CancellationToken ct)
    {
        if (_minorVersion < 2)
            throw new NfsException("COPY requires NFSv4.2+.");

        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(srcPath))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeOp(NfsV4Op.SaveFh));
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(dstPath))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeCopyOp(srcOffset, dstOffset, count));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "copy", Operations = ops }, ct);
        EnsureCompoundOk(resp, "COPY");

        var result = resp.Results[^1].Data!;
        var callbackCount = result.UInt();
        for (var i = 0; i < callbackCount; i++)
            NfsV4StateId.Decode(result);
        return result.ULong(); // count written
    }

    /// <summary>CLONE — server-side copy-on-write / reflink (v4.2).</summary>
    public async Task CloneAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        if (_minorVersion < 2)
            throw new NfsException("CLONE requires NFSv4.2+.");

        var ops = new List<NfsV4Operation>();
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(srcPath))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeOp(NfsV4Op.SaveFh));
        ops.Add(MakeOp(NfsV4Op.PutRootFh));
        foreach (var part in SplitPath(dstPath))
            ops.Add(MakeLookupOp(part));
        ops.Add(MakeCloneOp(0, 0, 0));

        var resp = await CompoundAsync(new NfsV4CompoundRequest { Tag = "clone", Operations = ops }, ct);
        EnsureCompoundOk(resp, "CLONE");
    }

    // --- Operation Builders ---

    private static NfsV4Operation MakeOp(NfsV4Op op, Action<XdrWriter>? encode = null)
    {
        if (encode == null)
            return new NfsV4Operation { Op = op };

        var w = new XdrWriter();
        encode(w);
        return new NfsV4Operation { Op = op, Args = w.ToArray() };
    }

    private static NfsV4Operation MakeLookupOp(string name) =>
        MakeOp(NfsV4Op.Lookup, w => w.Str(name));

    private static NfsV4Operation MakeGetAttrOp(params uint[] attrs) =>
        MakeOp(NfsV4Op.GetAttr, w => NfsV4Bitmap.Of(attrs).Encode(w));

    private NfsV4Operation MakeReadOp(ulong offset, uint count, NfsV4StateId stateId) =>
        MakeOp(NfsV4Op.Read, w => { stateId.Encode(w); w.ULong(offset); w.UInt(count); });

    private NfsV4Operation MakeWriteOp(ulong offset, byte[] data, NfsWriteStableHow stableHow, NfsV4StateId stateId) =>
        MakeOp(NfsV4Op.Write, w => { stateId.Encode(w); w.ULong(offset); w.UInt((uint)stableHow); w.Opaque(data); });

    private NfsV4Operation MakeCommitOp(ulong offset, uint count) =>
        MakeOp(NfsV4Op.Commit, w => { w.ULong(offset); w.UInt(count); });

    private NfsV4Operation MakeOpenOp(string name, NfsV4OpenShareAccess access, NfsV4OpenShareDeny deny) =>
        MakeOp(NfsV4Op.Open, w =>
        {
            w.UInt(0); // seqid
            w.UInt((uint)access);
            w.UInt((uint)deny);
            w.ULong(_clientId); // owner.clientid
            w.Str($"owner-{_clientId}-{_sequenceId++}"); // owner.owner
            w.UInt(0); // OPEN4_NOCREATE
            w.UInt((uint)NfsV4OpenClaimType.Null);
            w.Str(name);
        });

    private NfsV4Operation MakeCloseOp(NfsV4StateId stateId) =>
        MakeOp(NfsV4Op.Close, w => { w.UInt(0); stateId.Encode(w); });

    private NfsV4Operation MakeCreateOp(string name, NfsV4FType type, NfsSetAttributes? attrs) =>
        MakeOp(NfsV4Op.Create, w =>
        {
            w.UInt((uint)type);
            w.Str(name);
            if (attrs?.Mode.HasValue == true)
            {
                NfsV4Bitmap.Of(NfsV4Attr.Mode).Encode(w);
                w.UInt(4); // attr data length
                w.UInt(attrs.Mode.Value);
            }
            else
            {
                NfsV4Bitmap.Of().Encode(w);
                w.UInt(0); // empty attr data
            }
        });

    private NfsV4Operation MakeRemoveOp(string name) =>
        MakeOp(NfsV4Op.Remove, w => w.Str(name));

    private NfsV4Operation MakeRenameOp(string fromName, string toName) =>
        MakeOp(NfsV4Op.Rename, w => { w.Str(fromName); w.Str(toName); });

    private NfsV4Operation MakeReadDirOp(ulong cookie) =>
        MakeOp(NfsV4Op.ReadDir, w =>
        {
            w.ULong(cookie);
            w.ULong(0); // cookieverf (8 bytes zero)
            w.UInt(0);
            w.UInt(0xffffffff); // max count
            NfsV4Bitmap.Of(NfsV4Attr.Type, NfsV4Attr.Fileid).Encode(w);
        });

    private NfsV4Operation MakeSetClientIdOp() =>
        MakeOp(NfsV4Op.SetClientId, w =>
        {
            var id = $"nfs-sharp-{_ip}-{Environment.ProcessId}";
            w.Opaque(System.Text.Encoding.UTF8.GetBytes(id));
            w.FixedBytes(_clientVerifier);
            w.UInt(0); // callback id
            w.UInt(0); // callback program
            w.UInt(0); // callback ident count
        });

    private NfsV4Operation MakeExchangeIdOp() =>
        MakeOp(NfsV4Op.ExchangeId, w =>
        {
            var id = $"nfs-sharp-{_ip}-{Environment.ProcessId}";
            w.Opaque(System.Text.Encoding.UTF8.GetBytes(id));
            w.ULong(0); // sequence id
            w.UInt(0); // flags
            w.UInt(0); // state_protect (SP4_NONE)
            w.UInt(0); // impl_id count
        });

    private NfsV4Operation MakeCreateSessionOp() =>
        MakeOp(NfsV4Op.CreateSession, w =>
        {
            w.ULong(_clientId);
            w.ULong(_sequenceId);
            w.UInt(0); // flags
            w.UInt(0); // fore channel
            w.UInt(0); // back channel
            w.UInt(0); // cb_program
        });

    private NfsV4Operation MakeSeekOp(ulong offset, bool seekHole) =>
        MakeOp(NfsV4Op.Seek, w =>
        {
            NfsV4StateId.Anonymous.Encode(w);
            w.ULong(offset);
            w.UInt(seekHole ? 1u : 0u); // SEEK_HOLE=1, SEEK_DATA=0
        });

    private NfsV4Operation MakeAllocateOp(ulong offset, ulong length) =>
        MakeOp(NfsV4Op.Allocate, w =>
        {
            NfsV4StateId.Anonymous.Encode(w);
            w.ULong(offset);
            w.ULong(length);
        });

    private NfsV4Operation MakeDeallocateOp(ulong offset, ulong length) =>
        MakeOp(NfsV4Op.Deallocate, w =>
        {
            NfsV4StateId.Anonymous.Encode(w);
            w.ULong(offset);
            w.ULong(length);
        });

    private NfsV4Operation MakeCopyOp(ulong srcOffset, ulong dstOffset, ulong count) =>
        MakeOp(NfsV4Op.Copy, w =>
        {
            w.UInt(0); w.FixedBytes(new byte[12]); // src stateid
            w.UInt(0); w.FixedBytes(new byte[12]); // dst stateid
            w.ULong(srcOffset);
            w.ULong(dstOffset);
            w.ULong(count);
            w.Bool(false); // consecutive not required
            w.Bool(true); // request synchronous copy
            w.UInt(0); // source_server count for intra-server copy
        });

    private NfsV4Operation MakeCloneOp(ulong srcOffset, ulong dstOffset, ulong count) =>
        MakeOp(NfsV4Op.Clone, w =>
        {
            NfsV4StateId.Anonymous.Encode(w);
            NfsV4StateId.Anonymous.Encode(w);
            w.ULong(srcOffset);
            w.ULong(dstOffset);
            w.ULong(count);
        });

    // --- Encoding / Decoding ---

    private void EncodeCompoundRequest(XdrWriter writer, NfsV4CompoundRequest request)
    {
        writer.Str(request.Tag);
        writer.UInt(request.MinorVersion);
        writer.UInt((uint)request.Operations.Count);

        foreach (var op in request.Operations)
        {
            writer.UInt((uint)op.Op);
            if (op.Args is { Length: > 0 })
                writer.Raw(op.Args);
        }
    }

    private NfsV4CompoundResponse DecodeCompoundResponse(XdrReader reader)
        => NfsV4CompoundResponse.Decode(reader);

    private NfsV4Fattr DecodeFattr(XdrReader reader)
    {
        var bitmap = NfsV4Bitmap.Decode(reader);
        var attrReader = new XdrReader(reader.Opaque());
        var attr = new NfsV4Fattr();

        if (bitmap.HasAttr(NfsV4Attr.Type))
            attr = attr with { Type = (NfsV4FType)attrReader.UInt() };
        if (bitmap.HasAttr(NfsV4Attr.Change))
            attr = attr with { Change = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.Size))
            attr = attr with { Size = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.Fileid))
            attr = attr with { Fileid = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.Mode))
            attr = attr with { Mode = attrReader.UInt() };
        if (bitmap.HasAttr(NfsV4Attr.Numinlinks))
            attr = attr with { Numinlinks = attrReader.UInt() };
        if (bitmap.HasAttr(NfsV4Attr.Owner))
            attr = attr with { Owner = attrReader.Str() };
        if (bitmap.HasAttr(NfsV4Attr.OwnerGroup))
            attr = attr with { OwnerGroup = attrReader.Str() };
        if (bitmap.HasAttr(NfsV4Attr.SpaceAvail))
            attr = attr with { SpaceAvail = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.SpaceFree))
            attr = attr with { SpaceFree = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.SpaceTotal))
            attr = attr with { SpaceTotal = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.SpaceUsed))
            attr = attr with { SpaceUsed = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.Maxfilesize))
            attr = attr with { Maxfilesize = attrReader.ULong() };
        if (bitmap.HasAttr(NfsV4Attr.Maxread))
            attr = attr with { Maxread = attrReader.UInt() };
        if (bitmap.HasAttr(NfsV4Attr.Maxwrite))
            attr = attr with { Maxwrite = attrReader.UInt() };
        if (bitmap.HasAttr(NfsV4Attr.LeaseTime))
            attr = attr with { LeaseTime = attrReader.UInt() };

        return attr;
    }

    private List<NfsV4DirEntry> DecodeReadDirEntries(XdrReader reader)
    {
        var entries = new List<NfsV4DirEntry>();
        reader.FixedBytes(8); // cookie verifier

        while (reader.Bool()) // value_follows
        {
            var cookie = reader.ULong();
            var name = reader.Str();
            var attr = DecodeFattr(reader);

            entries.Add(new NfsV4DirEntry { Cookie = cookie, Name = name, Attributes = attr });
        }
        reader.Bool(); // eof

        return entries;
    }

    // --- Connection Infrastructure ---

    private async Task<int> DiscoverNfsPortAsync(IPAddress ip, NfsClientOptions options, CancellationToken ct)
    {
        await using var pm = await OpenAsync(options.PortmapPort, ct);
        var writer = new XdrWriter();
        writer.UInt(ProgNfs);
        writer.UInt(4);
        writer.UInt(IpprotoTcp);
        writer.UInt(0);
        var reader = await CallRawAsync(pm, ProgPortmap, 2, 3, writer.ToArray(), ct);
        var port = (int)reader.UInt();
        return port > 0 ? port : DefaultNfsPort;
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
        writer.UInt(1);
        writer.Opaque(_credBody);
        writer.UInt(0);
        writer.UInt(0);
        writer.Raw(args);

        await SendRecordAsync(conn.Stream, writer.ToArray(), ct);
        var reply = await RecvRecordAsync(conn.Stream, ct);

        var reader = new XdrReader(reply);
        reader.UInt(); // xid
        reader.UInt(); // msg type
        reader.UInt(); // reply stat
        reader.UInt(); // verifier flavor
        reader.SkipOpaque();
        reader.UInt(); // accept stat
        return reader;
    }

    private Conn RequireNfs() => _nfs ?? throw new NfsException("NFS connection is not established.");

    private async Task<Conn> OpenAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(_ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_ip, port, ct);
            if (_options.TcpNoDelay) socket.NoDelay = true;
            return new Conn(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
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

    private static async Task<byte[]> RecvRecordAsync(Stream stream, CancellationToken ct)
    {
        using var aggregate = new MemoryStream();
        var header = new byte[4];
        var last = false;
        while (!last)
        {
            await stream.ReadExactlyAsync(header, ct);
            var marker = BinaryPrimitives.ReadUInt32BigEndian(header);
            last = (marker & 0x8000_0000u) != 0;
            var length = (int)(marker & 0x7FFF_FFFF);
            if (length < 0 || length > MaxRpcRecordLength)
                throw new NfsException($"Invalid RPC fragment length: {length}.");
            var fragment = new byte[length];
            await stream.ReadExactlyAsync(fragment, ct);
            aggregate.Write(fragment, 0, length);
        }
        return aggregate.ToArray();
    }

    private static void EnsureCompoundOk(NfsV4CompoundResponse resp, string operation)
    {
        if (resp.Status != NfsV4Status.Ok)
            throw new NfsException($"{operation} failed (nfsstat4={NfsV4Status.Describe(resp.Status)}).", resp.Status);

        for (var i = 0; i < resp.Results.Count; i++)
        {
            if (resp.Results[i].Status != NfsV4Status.Ok)
                throw new NfsException($"{operation} op[{i}] {resp.Results[i].Op} failed (nfsstat4={NfsV4Status.Describe(resp.Results[i].Status)}).", resp.Results[i].Status);
        }
    }

    private static byte[] BuildAuthSysBody(NfsClientOptions options)
    {
        string machine;
        try { machine = Dns.GetHostName(); } catch { machine = "nfs-sharp"; }
        if (machine.Length > 255) machine = machine[..255];
        var writer = new XdrWriter();
        writer.UInt(0);
        writer.Str(machine);
        writer.UInt(options.UserId);
        writer.UInt(options.GroupId);
        var groups = options.AuxiliaryGroups ?? Array.Empty<uint>();
        writer.UInt((uint)groups.Count);
        foreach (var g in groups) writer.UInt(g);
        return writer.ToArray();
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

    private static IEnumerable<string> SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "/")
            yield break;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") throw new NfsException("Parent path traversal is not allowed.");
            yield return part;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_nfs is not null)
        {
            await _nfs.DisposeAsync();
            _nfs = null;
        }
        _rpcLock.Dispose();
    }

    private sealed class Conn : IAsyncDisposable
    {
        private readonly Socket _socket;
        public Conn(Socket socket) { _socket = socket; Stream = new NetworkStream(socket, ownsSocket: false); }
        public NetworkStream Stream { get; }
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync(); } catch { }
            try { _socket.Dispose(); } catch { }
        }
    }
}
