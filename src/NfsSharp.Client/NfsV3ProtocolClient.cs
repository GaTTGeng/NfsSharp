using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Encodes NFSv3 procedure calls over RPC and decodes the XDR replies.</summary>
internal sealed class NfsV3ProtocolClient
{
    // Cap on RPC record reassembly to bound memory when a peer sends oversized fragments.
    private const int MaxRpcRecordLength = 64 * 1024 * 1024;
    // ACCESS3 bits defined by the protocol; anything outside this mask is rejected up front.
    private const NfsAccessMode ValidAccessMask =
        NfsAccessMode.Read |
        NfsAccessMode.Lookup |
        NfsAccessMode.Modify |
        NfsAccessMode.Extend |
        NfsAccessMode.Delete |
        NfsAccessMode.Execute;

    private readonly IRpcCallClient _rpcClient;
    private readonly NfsClientOptions _options;
    private readonly NfsDirectoryCache _directoryCache;

    internal NfsV3ProtocolClient(
        IRpcCallClient rpcClient,
        NfsClientOptions options,
        NfsDirectoryCache directoryCache)
    {
        _rpcClient = rpcClient;
        _options = options;
        _directoryCache = directoryCache;
    }

    /// <summary>LOOKUP a name in a directory handle.</summary>
    internal async Task<NfsLookup> LookupAsync(byte[] directoryHandle, string name, CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        var writer = new XdrWriter();
        writer.Opaque(directoryHandle);
        writer.Str(name);
        var reader = await CallAsync(NfsRpcConstants.NfsLookup, writer, ct);
        EnsureOk(reader.UInt(), $"LOOKUP \"{name}\" failed");
        // LOOKUP reply: object handle, its post_op_attr, then the directory's post_op_attr.
        var handle = reader.Opaque();
        var attributes = ReadPostOpAttr(reader);
        ReadPostOpAttr(reader); // dir_attributes (post_op_attr) — currently unused
        return new NfsLookup(handle, attributes);
    }

    /// <summary>GETATTR — full fattr3 for a file handle.</summary>
    internal async Task<NfsFattr> GetAttributesAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var writer = HandleArguments(fileHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsGetAttr, writer, ct);
        EnsureOk(reader.UInt(), "GETATTR failed");
        return ReadFattr3(reader);
    }

    /// <summary>FSSTAT — storage capacity and availability for a file handle.</summary>
    internal async Task<NfsFileSystemStat> GetFileSystemStatAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsFsstat, HandleArguments(fileHandle), ct);
        EnsureOk(reader.UInt(), "FSSTAT failed");
        ReadPostOpAttr(reader);
        return new NfsFileSystemStat(
            reader.ULong(),
            reader.ULong(),
            reader.ULong(),
            reader.ULong(),
            reader.ULong(),
            reader.ULong(),
            TimeSpan.FromSeconds(reader.UInt()));
    }

    /// <summary>FSINFO — server transfer preferences and feature flags for a file handle.</summary>
    internal async Task<NfsFileSystemInfo> GetFileSystemInfoAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsFsinfo, HandleArguments(fileHandle), ct);
        EnsureOk(reader.UInt(), "FSINFO failed");
        // FSINFO reply: post_op_attr, transfer-size triplets, time_delta, then property bits.
        ReadPostOpAttr(reader);
        var maxReadSize = reader.UInt();
        var preferredReadSize = reader.UInt();
        var readMultipleSize = reader.UInt();
        var maxWriteSize = reader.UInt();
        var preferredWriteSize = reader.UInt();
        var writeMultipleSize = reader.UInt();
        var preferredReaddirSize = reader.UInt();
        var maxFileSize = reader.ULong();
        var seconds = reader.UInt();
        var nanoseconds = reader.UInt();
        // time_delta is a normalized duration; a nanosecond field of 1s or more is malformed.
        if (nanoseconds >= 1_000_000_000)
            throw new NfsException($"FSINFO returned an invalid time_delta nanoseconds value {nanoseconds}.");
        var properties = reader.UInt();
        return new NfsFileSystemInfo
        {
            MaxReadSize = maxReadSize,
            PreferredReadSize = preferredReadSize,
            ReadMultipleSize = readMultipleSize,
            MaxWriteSize = maxWriteSize,
            PreferredWriteSize = preferredWriteSize,
            WriteMultipleSize = writeMultipleSize,
            PreferredReaddirSize = preferredReaddirSize,
            MaxFileSize = maxFileSize,
            TimeDelta = TimeSpan.FromSeconds(seconds).Add(TimeSpan.FromTicks(nanoseconds / 100)),
            Properties = properties
        };
    }

    /// <summary>PATHCONF — POSIX path constraints for a file handle.</summary>
    internal async Task<NfsPathConf> GetPathConfAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsPathconf, HandleArguments(fileHandle), ct);
        EnsureOk(reader.UInt(), "PATHCONF failed");
        ReadPostOpAttr(reader);
        return new NfsPathConf
        {
            LinkMax = reader.UInt(),
            NameMax = reader.UInt(),
            NoTrunc = reader.Bool(),
            ChownRestricted = reader.Bool(),
            CaseInsensitive = reader.Bool(),
            CasePreserving = reader.Bool()
        };
    }

    /// <summary>ACCESS — check the requested mask and return what the server grants.</summary>
    internal async Task<NfsAccessMode> AccessAsync(
        byte[] fileHandle,
        NfsAccessMode desired,
        CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var invalid = desired & ~ValidAccessMask;
        if (invalid != NfsAccessMode.None)
            throw new NfsException($"Invalid ACCESS mask: 0x{(uint)desired:X}.");
        var writer = HandleArguments(fileHandle);
        writer.UInt((uint)desired);
        var reader = await CallAsync(NfsRpcConstants.NfsAccess, writer, ct);
        EnsureOk(reader.UInt(), "ACCESS failed");
        ReadPostOpAttr(reader);
        var granted = (NfsAccessMode)reader.UInt();
        // A grant outside the requested mask is a protocol violation; surface it instead of hiding it.
        if ((granted & ~desired) != NfsAccessMode.None)
        {
            throw new NfsException(
                $"ACCESS returned grant 0x{(uint)granted:X} outside requested mask 0x{(uint)desired:X}.");
        }

        return granted;
    }

    /// <summary>READLINK — read the target string of a symbolic link handle.</summary>
    internal async Task<string> ReadLinkAsync(byte[] symlinkHandle, CancellationToken ct)
    {
        ValidateHandle(symlinkHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsReadlink, HandleArguments(symlinkHandle), ct);
        EnsureOk(reader.UInt(), "READLINK failed");
        ReadPostOpAttr(reader);
        return reader.Str();
    }

    /// <summary>COMMIT — flush cached data to stable storage and return the write verifier.</summary>
    internal async Task<NfsCommitResult> CommitAsync(
        byte[] fileHandle,
        ulong offset,
        uint count,
        CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var writer = HandleArguments(fileHandle);
        writer.ULong(offset);
        writer.UInt(count);
        var reader = await CallAsync(NfsRpcConstants.NfsCommit, writer, ct);
        EnsureOk(reader.UInt(), "COMMIT failed");
        // COMMIT reply: wcc_data followed by the 8-byte write verifier identifying the server's stable store.
        ReadWccData(reader);
        return new NfsCommitResult(reader.FixedBytes(8));
    }

    /// <summary>SYMLINK — create a symbolic link in a directory handle.</summary>
    internal async Task<NfsLookup> CreateSymbolicLinkAsync(
        byte[] directoryHandle,
        string name,
        string targetPath,
        NfsSetAttributes? attributes,
        CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var writer = HandleArguments(directoryHandle);
        writer.Str(name);
        writer.Str(targetPath);
        WriteSattr3(writer, attributes ?? NfsSetAttributes.FileDefault);
        var reader = await CallAsync(NfsRpcConstants.NfsSymlink, writer, ct);
        EnsureOk(reader.UInt(), $"SYMLINK \"{name}\" failed");
        _directoryCache.Invalidate(directoryHandle);
        return ReadDiropOk(reader);
    }

    /// <summary>LINK — create a hard link to an existing file handle.</summary>
    internal async Task CreateHardLinkAsync(
        byte[] targetHandle,
        byte[] directoryHandle,
        string name,
        CancellationToken ct)
    {
        ValidateHandle(targetHandle);
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        // LINK arguments are ordered target handle first, then the destination directory.
        var writer = HandleArguments(targetHandle);
        writer.Opaque(directoryHandle);
        writer.Str(name);
        var reader = await CallAsync(NfsRpcConstants.NfsLink, writer, ct);
        EnsureOk(reader.UInt(), $"LINK \"{name}\" failed");
        ReadPostOpAttr(reader);
        ReadWccData(reader);
        _directoryCache.Invalidate(directoryHandle);
    }

    /// <summary>MKNOD — create a device node, FIFO, or socket in a directory handle.</summary>
    internal async Task<NfsLookup> CreateNodeAsync(
        byte[] directoryHandle,
        string name,
        NfsType type,
        NfsSetAttributes? attributes,
        uint? majorDevice,
        uint? minorDevice,
        CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        if (type is not (NfsType.Blk or NfsType.Chr or NfsType.Sock or NfsType.Fifo))
            throw new NfsException($"MKNOD type must be Blk, Chr, Sock, or Fifo, got {type}.");
        var writer = HandleArguments(directoryHandle);
        writer.Str(name);
        writer.UInt((uint)type);
        WriteSattr3(writer, attributes ?? NfsSetAttributes.FileDefault);
        // specdata4 (major/minor) is present on the wire only for block/char devices.
        if (type is NfsType.Blk or NfsType.Chr)
        {
            writer.UInt(majorDevice ?? 0);
            writer.UInt(minorDevice ?? 0);
        }

        var reader = await CallAsync(NfsRpcConstants.NfsMknod, writer, ct);
        EnsureOk(reader.UInt(), $"MKNOD \"{name}\" failed");
        _directoryCache.Invalidate(directoryHandle);
        return ReadDiropOk(reader);
    }

    /// <summary>READDIRPLUS — full entry list including attributes and handles, with optional caching.</summary>
    internal async Task<List<NfsEntryPlus>> ReadDirectoryPlusAsync(byte[] directoryHandle, CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        // Serve from cache when enabled and fresh; callers receive a defensive clone.
        if (_directoryCache.TryGet(directoryHandle, out var cached))
            return cached;

        // Capture the mutation generation before reading so a concurrent mutation
        // between here and Store() prevents caching possibly torn results.
        var cacheGeneration = _directoryCache.CaptureMutationGeneration();
        var entries = new List<NfsEntryPlus>();
        ulong cookie = 0;
        var verifier = new byte[8];
        // READDIRPLUS is paged via the opaque cookie; loop until the server reports eof.
        while (true)
        {
            // Remember the request cookie so a non-advancing page can be rejected below.
            var requestCookie = cookie;
            var pageCount = 0;
            var writer = HandleArguments(directoryHandle);
            writer.ULong(cookie);
            writer.FixedBytes(verifier);
            writer.UInt((uint)_options.ReaddirCount);
            writer.UInt((uint)_options.ReaddirCount);
            var reader = await CallAsync(NfsRpcConstants.NfsReadDirPlus, writer, ct);
            EnsureOk(reader.UInt(), "READDIRPLUS failed");
            ReadPostOpAttr(reader);
            // Echo the returned verifier on every subsequent page of this listing.
            verifier = reader.FixedBytes(8);
            while (reader.Bool())
            {
                var fileId = reader.ULong();
                var name = reader.Str();
                cookie = reader.ULong();
                var attributes = reader.Bool() ? ReadFattr3(reader) : null;
                var handle = reader.Bool() ? reader.Opaque() : null;
                entries.Add(new NfsEntryPlus(name, fileId, attributes, handle));
                pageCount++;
            }

            var eof = reader.Bool();
            EnsureDirectoryReadProgress(requestCookie, cookie, pageCount, eof, "READDIRPLUS");
            if (eof)
                break;
        }

        // Store only when no mutation raced the read; otherwise the listing is returned uncached.
        _directoryCache.Store(directoryHandle, entries, cacheGeneration);
        return entries;
    }

    /// <summary>READDIR — name and file id entries only (no attributes or handles).</summary>
    internal async Task<List<NfsEntry>> ReadDirectoryAsync(byte[] directoryHandle, CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        var entries = new List<NfsEntry>();
        ulong cookie = 0;
        var verifier = new byte[8];
        // Same cookie-paging loop as READDIRPLUS; results are never cached here.
        while (true)
        {
            // Remember the request cookie so a stuck server page can be detected below.
            var requestCookie = cookie;
            var pageCount = 0;
            var writer = HandleArguments(directoryHandle);
            writer.ULong(cookie);
            writer.FixedBytes(verifier);
            writer.UInt((uint)_options.ReaddirCount);
            var reader = await CallAsync(NfsRpcConstants.NfsReadDir, writer, ct);
            EnsureOk(reader.UInt(), "READDIR failed");
            ReadPostOpAttr(reader);
            // The returned verifier must be echoed on every subsequent page of this listing.
            verifier = reader.FixedBytes(8);
            while (reader.Bool())
            {
                var fileId = reader.ULong();
                var name = reader.Str();
                cookie = reader.ULong();
                entries.Add(new NfsEntry(name, fileId));
                pageCount++;
            }

            var eof = reader.Bool();
            EnsureDirectoryReadProgress(requestCookie, cookie, pageCount, eof, "READDIR");
            if (eof)
                break;
        }

        return entries;
    }

    /// <summary>READ a single chunk into the caller's buffer; returns bytes read and the EOF flag.</summary>
    internal async Task<(int BytesRead, bool Eof)> ReadAsync(
        byte[] fileHandle,
        ulong offset,
        Memory<byte> destination,
        CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        if (destination.Length == 0)
            return (0, false);
        // Enforce the configured per-request limit so the server never sees a larger count.
        if (destination.Length > _options.MaxReadSize)
        {
            throw new NfsException(
                $"READ request length {destination.Length} exceeds MaxReadSize {_options.MaxReadSize}.");
        }

        var writer = HandleArguments(fileHandle);
        writer.ULong(offset);
        writer.UInt((uint)destination.Length);
        var reader = await CallAsync(NfsRpcConstants.NfsRead, writer, ct);
        EnsureOk(reader.UInt(), "READ failed");
        ReadPostOpAttr(reader);
        // Reply order: count, eof flag, then the data opaque.
        var count = reader.UInt();
        var eof = reader.Bool();
        if (count > destination.Length)
            throw new NfsException($"READ returned count {count} for {destination.Length} byte request.");
        // Cap the opaque length we are willing to decode as a defense against oversized replies.
        var data = reader.Opaque(Math.Min(destination.Length, MaxRpcRecordLength));
        // count and the data payload must agree; a mismatch means a corrupt reply.
        if (data.Length != count)
            throw new NfsException($"READ returned {data.Length} bytes but count was {count}.");
        data.CopyTo(destination);
        return ((int)count, eof);
    }

    /// <summary>WRITE a single chunk and return the written count, stability, and verifier.</summary>
    internal async Task<NfsWriteResult> WriteAsync(
        byte[] fileHandle,
        ulong offset,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateHandle(fileHandle);
        // Zero-length WRITE is a no-op; skip the round trip and report the requested stability.
        if (data.Length == 0)
            return new NfsWriteResult(0, _options.StableHow, Array.Empty<byte>());
        if (data.Length > _options.MaxWriteSize)
        {
            throw new NfsException(
                $"WRITE request length {data.Length} exceeds MaxWriteSize {_options.MaxWriteSize}.");
        }

        // WRITE args: handle, offset, count, stable_how, then the data opaque.
        var writer = HandleArguments(fileHandle);
        writer.ULong(offset);
        writer.UInt((uint)data.Length);
        writer.UInt((uint)_options.StableHow);
        writer.Opaque(data.Span);
        var reader = await CallAsync(NfsRpcConstants.NfsWrite, writer, ct);
        EnsureOk(reader.UInt(), "WRITE failed");
        ReadWccData(reader);
        // Reply: wcc_data, written count, the stability actually committed, and the write verifier.
        var count = reader.UInt();
        var committed = (NfsWriteStableHow)reader.UInt();
        var verifier = reader.FixedBytes(8);
        if (count > data.Length)
            throw new NfsException($"WRITE returned invalid count {count} for {data.Length} byte request.");
        // File content changed: drop cached directory listings that carry this handle's attributes.
        _directoryCache.InvalidateForMutation(fileHandle);
        return new NfsWriteResult((int)count, committed, verifier);
    }

    /// <summary>CREATE a file in a directory handle (createmode UNCHECKED).</summary>
    internal async Task<NfsLookup> CreateFileAsync(
        byte[] directoryHandle,
        string name,
        NfsSetAttributes? attributes,
        CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        var writer = HandleArguments(directoryHandle);
        writer.Str(name);
        writer.UInt(1); // createmode = UNCHECKED: truncate if the file already exists
        WriteSattr3(writer, attributes ?? NfsSetAttributes.FileDefault);
        var reader = await CallAsync(NfsRpcConstants.NfsCreate, writer, ct);
        EnsureOk(reader.UInt(), $"CREATE \"{name}\" failed");
        _directoryCache.Invalidate(directoryHandle);
        return ReadDiropOk(reader);
    }

    /// <summary>MKDIR — create a directory in a directory handle.</summary>
    internal async Task<NfsLookup> CreateDirectoryAsync(
        byte[] directoryHandle,
        string name,
        NfsSetAttributes? attributes,
        CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        var writer = HandleArguments(directoryHandle);
        writer.Str(name);
        WriteSattr3(writer, attributes ?? NfsSetAttributes.DirectoryDefault);
        var reader = await CallAsync(NfsRpcConstants.NfsMkdir, writer, ct);
        EnsureOk(reader.UInt(), $"MKDIR \"{name}\" failed");
        _directoryCache.Invalidate(directoryHandle);
        return ReadDiropOk(reader);
    }

    /// <summary>SETATTR for a file handle, optionally guarded by the expected ctime.</summary>
    internal async Task SetAttributesAsync(
        byte[] fileHandle,
        NfsSetAttributes attributes,
        NfsTimestamp? guardCtime,
        CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        ArgumentNullException.ThrowIfNull(attributes);
        var writer = HandleArguments(fileHandle);
        WriteSattr3(writer, attributes);
        WriteSattrGuard3(writer, guardCtime);
        var reader = await CallAsync(NfsRpcConstants.NfsSetAttr, writer, ct);
        EnsureOk(reader.UInt(), "SETATTR failed");
        // wcc_data reports before/after metadata; attributes cached for this handle are now stale.
        ReadWccData(reader);
        _directoryCache.InvalidateForMutation(fileHandle);
    }

    /// <summary>REMOVE or RMDIR an entry in a directory handle (selected by procedure).</summary>
    internal async Task RemoveAsync(
        uint procedure,
        byte[] parentHandle,
        string name,
        string message,
        CancellationToken ct)
    {
        ValidateHandle(parentHandle);
        NfsPathResolver.ValidateName(name);
        var writer = HandleArguments(parentHandle);
        writer.Str(name);
        var reader = await CallAsync(procedure, writer, ct);
        EnsureOk(reader.UInt(), message);
        ReadWccData(reader);
        _directoryCache.Invalidate(parentHandle);
    }

    /// <summary>RENAME an entry between two directory handles.</summary>
    internal async Task RenameAsync(
        byte[] sourceParent,
        string sourceName,
        byte[] targetParent,
        string targetName,
        string message,
        CancellationToken ct)
    {
        ValidateHandle(sourceParent);
        ValidateHandle(targetParent);
        NfsPathResolver.ValidateName(sourceName);
        NfsPathResolver.ValidateName(targetName);
        var writer = HandleArguments(sourceParent);
        writer.Str(sourceName);
        writer.Opaque(targetParent);
        writer.Str(targetName);
        var reader = await CallAsync(NfsRpcConstants.NfsRename, writer, ct);
        EnsureOk(reader.UInt(), message);
        ReadWccData(reader); // fromdir wcc_data
        ReadWccData(reader); // todir wcc_data
        // Both directories can list the renamed entry, so invalidate both listings.
        _directoryCache.Invalidate(sourceParent);
        _directoryCache.Invalidate(targetParent);
    }

    /// <summary>Reject directory pages that would spin forever without advancing the cookie.</summary>
    internal static void EnsureDirectoryReadProgress(
        ulong requestCookie,
        ulong responseCookie,
        int entryCount,
        bool eof,
        string procedure)
    {
        if (!eof && (entryCount == 0 || responseCookie == requestCookie))
        {
            throw new NfsException(
                $"{procedure} returned a non-terminal page without advancing its cookie.");
        }
    }

    /// <summary>Reject empty or missing NFS file handles before they reach the wire.</summary>
    internal static void ValidateHandle(byte[] handle)
    {
        if (handle is null || handle.Length == 0)
            throw new NfsException("NFS file handle is empty.");
    }

    /// <summary>Issue one NFS program/version 3 call on the shared RPC client.</summary>
    private Task<XdrReader> CallAsync(uint procedure, XdrWriter arguments, CancellationToken ct) =>
        _rpcClient.CallAsync(
            NfsRpcConstants.ProgNfs,
            NfsRpcConstants.VerNfs,
            procedure,
            arguments.ToArray(),
            ct);

    /// <summary>Start an argument writer already containing the leading fhandle3.</summary>
    private static XdrWriter HandleArguments(byte[] handle)
    {
        var writer = new XdrWriter();
        writer.Opaque(handle);
        return writer;
    }

    /// <summary>Decode the diropok3 result shared by CREATE, MKDIR, SYMLINK, and MKNOD.</summary>
    private static NfsLookup ReadDiropOk(XdrReader reader)
    {
        // A false handle flag yields an empty handle (some servers omit it on failure paths).
        var handle = reader.Bool() ? reader.Opaque() : Array.Empty<byte>();
        var attributes = ReadPostOpAttr(reader);
        ReadWccData(reader);
        return new NfsLookup(handle, attributes);
    }

    /// <summary>Decode a post_op_attr: a boolean presence flag followed by optional fattr3.</summary>
    private static NfsFattr? ReadPostOpAttr(XdrReader reader) =>
        reader.Bool() ? ReadFattr3(reader) : null;

    /// <summary>Decode fattr3 in wire order (timestamps last).</summary>
    private static NfsFattr ReadFattr3(XdrReader reader)
    {
        // Wire order is fixed: type/mode/nlink/uid/gid, size/used, rdev, fsid/fileid, then atime/mtime/ctime.
        var type = (NfsType)reader.UInt();
        var mode = reader.UInt();
        var linkCount = reader.UInt();
        var uid = reader.UInt();
        var gid = reader.UInt();
        var size = reader.ULong();
        var used = reader.ULong();
        reader.UInt(); // rdev specdata1
        reader.UInt(); // rdev specdata2
        var fileSystemId = reader.ULong();
        var fileId = reader.ULong();
        // fattr3 timestamps are mandatory fields; absence of attributes is signaled by post_op_attr,
        // never by a zero nfstime3. Unix epoch (0, 0) is therefore preserved as a real timestamp.
        var atime = ReadNfsTimestamp(reader);
        var mtime = ReadNfsTimestamp(reader);
        var ctime = ReadNfsTimestamp(reader);
        // NfsFattr exposes size as Int64; reject the unsupported upper range instead of truncating.
        if (size > long.MaxValue)
            throw new NfsException($"NFSv3 file size {size} exceeds the supported Int64 range.");
        return new NfsFattr(type, (long)size, mtime.ToDateTimeUtc())
        {
            Mode = mode,
            LinkCount = linkCount,
            Uid = uid,
            Gid = gid,
            Used = used,
            FileSystemId = fileSystemId,
            FileId = fileId,
            Atime = atime.ToDateTimeUtc(),
            Ctime = ctime.ToDateTimeUtc(),
            AtimeTimestamp = atime,
            MtimeTimestamp = mtime,
            CtimeTimestamp = ctime
        };
    }

    /// <summary>Decode wcc_data (pre-op attributes then post_op_attr); values are currently discarded.</summary>
    private static void ReadWccData(XdrReader reader)
    {
        if (reader.Bool())
        {
            reader.ULong(); // pre_op_size
            ReadNfsTimestamp(reader); // pre_op_mtime
            ReadNfsTimestamp(reader); // pre_op_ctime
        }

        ReadPostOpAttr(reader);
    }

    /// <summary>Decode an nfstime3. Epoch zero (0, 0) is a valid timestamp, not an absent marker.</summary>
    private static NfsTimestamp ReadNfsTimestamp(XdrReader reader)
    {
        var seconds = reader.UInt();
        var nanoseconds = reader.UInt();
        return new NfsTimestamp(seconds, nanoseconds);
    }

    /// <summary>Encode sattr3 in wire order; each optional field carries its own presence flag.</summary>
    private static void WriteSattr3(XdrWriter writer, NfsSetAttributes attributes)
    {
        // Fixed wire order: mode, uid, gid, size, atime, mtime — each as set_size3/set_time3.
        WriteOptionalUInt(writer, attributes.Mode);
        WriteOptionalUInt(writer, attributes.Uid);
        WriteOptionalUInt(writer, attributes.Gid);
        WriteOptionalULong(writer, attributes.Size);
        WriteOptionalTime(writer, attributes.Atime);
        WriteOptionalTime(writer, attributes.Mtime);
    }

    /// <summary>Encode sattrguard3: an optional ctime the server compares before applying SETATTR.</summary>
    private static void WriteSattrGuard3(XdrWriter writer, NfsTimestamp? guardCtime)
    {
        writer.Bool(guardCtime.HasValue);
        if (guardCtime.HasValue)
            WriteNfsTimestamp(writer, guardCtime.Value);
    }

    /// <summary>Encode an optional uint with its XDR presence flag.</summary>
    private static void WriteOptionalUInt(XdrWriter writer, uint? value)
    {
        writer.Bool(value.HasValue);
        if (value.HasValue)
            writer.UInt(value.Value);
    }

    /// <summary>Encode an optional hyper with its XDR presence flag.</summary>
    private static void WriteOptionalULong(XdrWriter writer, ulong? value)
    {
        writer.Bool(value.HasValue);
        if (value.HasValue)
            writer.ULong(value.Value);
    }

    /// <summary>Encode a set_time3: 0 = no change, 2 = set to the following nfstime3.</summary>
    private static void WriteOptionalTime(XdrWriter writer, DateTime? value)
    {
        if (!value.HasValue)
        {
            writer.UInt(0);
            return;
        }

        // The wire timestamp is UTC; interpret unspecified kinds as UTC rather than local.
        var utc = value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : value.Value.ToUniversalTime();
        writer.UInt(2);
        WriteNfsTimestamp(writer, NfsTimestamp.FromDateTime(utc));
    }

    private static void WriteNfsTimestamp(XdrWriter writer, NfsTimestamp value)
    {
        writer.UInt(value.Seconds);
        writer.UInt(value.Nanoseconds);
    }

    /// <summary>Throw <see cref="NfsException"/> unless the nfsstat3 status is OK.</summary>
    private static void EnsureOk(uint status, string message)
    {
        if (status != NfsV3Status.Ok)
            throw new NfsException($"{message} (nfsstat3={NfsV3Status.Describe(status)}).", status);
    }
}
