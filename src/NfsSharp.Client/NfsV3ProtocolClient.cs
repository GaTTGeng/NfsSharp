using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal sealed class NfsV3ProtocolClient
{
    private const int MaxRpcRecordLength = 64 * 1024 * 1024;
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

    internal async Task<NfsLookup> LookupAsync(byte[] directoryHandle, string name, CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        var writer = new XdrWriter();
        writer.Opaque(directoryHandle);
        writer.Str(name);
        var reader = await CallAsync(NfsRpcConstants.NfsLookup, writer, ct);
        EnsureOk(reader.UInt(), $"LOOKUP \"{name}\" failed");
        var handle = reader.Opaque();
        var attributes = ReadPostOpAttr(reader);
        ReadPostOpAttr(reader);
        return new NfsLookup(handle, attributes);
    }

    internal async Task<NfsFattr> GetAttributesAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var writer = HandleArguments(fileHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsGetAttr, writer, ct);
        EnsureOk(reader.UInt(), "GETATTR failed");
        return ReadFattr3(reader);
    }

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

    internal async Task<NfsFileSystemInfo> GetFileSystemInfoAsync(byte[] fileHandle, CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsFsinfo, HandleArguments(fileHandle), ct);
        EnsureOk(reader.UInt(), "FSINFO failed");
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
        if ((granted & ~desired) != NfsAccessMode.None)
        {
            throw new NfsException(
                $"ACCESS returned grant 0x{(uint)granted:X} outside requested mask 0x{(uint)desired:X}.");
        }

        return granted;
    }

    internal async Task<string> ReadLinkAsync(byte[] symlinkHandle, CancellationToken ct)
    {
        ValidateHandle(symlinkHandle);
        var reader = await CallAsync(NfsRpcConstants.NfsReadlink, HandleArguments(symlinkHandle), ct);
        EnsureOk(reader.UInt(), "READLINK failed");
        ReadPostOpAttr(reader);
        return reader.Str();
    }

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
        ReadWccData(reader);
        return new NfsCommitResult(reader.FixedBytes(8));
    }

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

    internal async Task CreateHardLinkAsync(
        byte[] targetHandle,
        byte[] directoryHandle,
        string name,
        CancellationToken ct)
    {
        ValidateHandle(targetHandle);
        ValidateHandle(directoryHandle);
        NfsPathResolver.ValidateName(name);
        var writer = HandleArguments(targetHandle);
        writer.Opaque(directoryHandle);
        writer.Str(name);
        var reader = await CallAsync(NfsRpcConstants.NfsLink, writer, ct);
        EnsureOk(reader.UInt(), $"LINK \"{name}\" failed");
        ReadPostOpAttr(reader);
        ReadWccData(reader);
        _directoryCache.Invalidate(directoryHandle);
    }

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

    internal async Task<List<NfsEntryPlus>> ReadDirectoryPlusAsync(byte[] directoryHandle, CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        if (_directoryCache.TryGet(directoryHandle, out var cached))
            return cached;

        var cacheGeneration = _directoryCache.CaptureMutationGeneration();
        var entries = new List<NfsEntryPlus>();
        ulong cookie = 0;
        var verifier = new byte[8];
        while (true)
        {
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

        _directoryCache.Store(directoryHandle, entries, cacheGeneration);
        return entries;
    }

    internal async Task<List<NfsEntry>> ReadDirectoryAsync(byte[] directoryHandle, CancellationToken ct)
    {
        ValidateHandle(directoryHandle);
        var entries = new List<NfsEntry>();
        ulong cookie = 0;
        var verifier = new byte[8];
        while (true)
        {
            var requestCookie = cookie;
            var pageCount = 0;
            var writer = HandleArguments(directoryHandle);
            writer.ULong(cookie);
            writer.FixedBytes(verifier);
            writer.UInt((uint)_options.ReaddirCount);
            var reader = await CallAsync(NfsRpcConstants.NfsReadDir, writer, ct);
            EnsureOk(reader.UInt(), "READDIR failed");
            ReadPostOpAttr(reader);
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

    internal async Task<(int BytesRead, bool Eof)> ReadAsync(
        byte[] fileHandle,
        ulong offset,
        Memory<byte> destination,
        CancellationToken ct)
    {
        ValidateHandle(fileHandle);
        if (destination.Length == 0)
            return (0, false);
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
        var count = reader.UInt();
        var eof = reader.Bool();
        if (count > destination.Length)
            throw new NfsException($"READ returned count {count} for {destination.Length} byte request.");
        var data = reader.Opaque(Math.Min(destination.Length, MaxRpcRecordLength));
        if (data.Length != count)
            throw new NfsException($"READ returned {data.Length} bytes but count was {count}.");
        data.CopyTo(destination);
        return ((int)count, eof);
    }

    internal async Task<NfsWriteResult> WriteAsync(
        byte[] fileHandle,
        ulong offset,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateHandle(fileHandle);
        if (data.Length == 0)
            return new NfsWriteResult(0, _options.StableHow, Array.Empty<byte>());
        if (data.Length > _options.MaxWriteSize)
        {
            throw new NfsException(
                $"WRITE request length {data.Length} exceeds MaxWriteSize {_options.MaxWriteSize}.");
        }

        var writer = HandleArguments(fileHandle);
        writer.ULong(offset);
        writer.UInt((uint)data.Length);
        writer.UInt((uint)_options.StableHow);
        writer.Opaque(data.Span);
        var reader = await CallAsync(NfsRpcConstants.NfsWrite, writer, ct);
        EnsureOk(reader.UInt(), "WRITE failed");
        ReadWccData(reader);
        var count = reader.UInt();
        var committed = (NfsWriteStableHow)reader.UInt();
        var verifier = reader.FixedBytes(8);
        if (count > data.Length)
            throw new NfsException($"WRITE returned invalid count {count} for {data.Length} byte request.");
        _directoryCache.InvalidateForMutation(fileHandle);
        return new NfsWriteResult((int)count, committed, verifier);
    }

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
        writer.UInt(1);
        WriteSattr3(writer, attributes ?? NfsSetAttributes.FileDefault);
        var reader = await CallAsync(NfsRpcConstants.NfsCreate, writer, ct);
        EnsureOk(reader.UInt(), $"CREATE \"{name}\" failed");
        _directoryCache.Invalidate(directoryHandle);
        return ReadDiropOk(reader);
    }

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
        ReadWccData(reader);
        _directoryCache.InvalidateForMutation(fileHandle);
    }

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
        ReadWccData(reader);
        ReadWccData(reader);
        _directoryCache.Invalidate(sourceParent);
        _directoryCache.Invalidate(targetParent);
    }

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

    internal static void ValidateHandle(byte[] handle)
    {
        if (handle is null || handle.Length == 0)
            throw new NfsException("NFS file handle is empty.");
    }

    private Task<XdrReader> CallAsync(uint procedure, XdrWriter arguments, CancellationToken ct) =>
        _rpcClient.CallAsync(
            NfsRpcConstants.ProgNfs,
            NfsRpcConstants.VerNfs,
            procedure,
            arguments.ToArray(),
            ct);

    private static XdrWriter HandleArguments(byte[] handle)
    {
        var writer = new XdrWriter();
        writer.Opaque(handle);
        return writer;
    }

    private static NfsLookup ReadDiropOk(XdrReader reader)
    {
        var handle = reader.Bool() ? reader.Opaque() : Array.Empty<byte>();
        var attributes = ReadPostOpAttr(reader);
        ReadWccData(reader);
        return new NfsLookup(handle, attributes);
    }

    private static NfsFattr? ReadPostOpAttr(XdrReader reader) =>
        reader.Bool() ? ReadFattr3(reader) : null;

    private static NfsFattr ReadFattr3(XdrReader reader)
    {
        var type = (NfsType)reader.UInt();
        var mode = reader.UInt();
        var linkCount = reader.UInt();
        var uid = reader.UInt();
        var gid = reader.UInt();
        var size = reader.ULong();
        var used = reader.ULong();
        reader.UInt();
        reader.UInt();
        var fileSystemId = reader.ULong();
        var fileId = reader.ULong();
        var atime = ReadNfsTimestamp(reader);
        var mtime = ReadNfsTimestamp(reader);
        var ctime = ReadNfsTimestamp(reader);
        if (size > long.MaxValue)
            throw new NfsException($"NFSv3 file size {size} exceeds the supported Int64 range.");
        return new NfsFattr(type, (long)size, mtime?.ToDateTimeUtc())
        {
            Mode = mode,
            LinkCount = linkCount,
            Uid = uid,
            Gid = gid,
            Used = used,
            FileSystemId = fileSystemId,
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
            reader.ULong();
            ReadNfsTimestamp(reader);
            ReadNfsTimestamp(reader);
        }

        ReadPostOpAttr(reader);
    }

    private static NfsTimestamp? ReadNfsTimestamp(XdrReader reader)
    {
        var seconds = reader.UInt();
        var nanoseconds = reader.UInt();
        return seconds == 0 && nanoseconds == 0 ? null : new NfsTimestamp(seconds, nanoseconds);
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
            writer.UInt(0);
            return;
        }

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

    private static void EnsureOk(uint status, string message)
    {
        if (status != NfsV3Status.Ok)
            throw new NfsException($"{message} (nfsstat3={NfsV3Status.Describe(status)}).", status);
    }
}
