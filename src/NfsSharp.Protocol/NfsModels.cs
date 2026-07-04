using Microsoft.Extensions.Logging;

namespace NfsSharp.Protocol;

/// <summary>NFS file types as defined by RFC 1813 ftype3.</summary>
public enum NfsType
{
    None = 0,
    Reg = 1,
    Dir = 2,
    Blk = 3,
    Chr = 4,
    Lnk = 5,
    Sock = 6,
    Fifo = 7
}

/// <summary>Stable write mode used by the NFSv3 WRITE procedure.</summary>
public enum NfsWriteStableHow
{
    Unstable = 0,
    DataSync = 1,
    FileSync = 2
}

/// <summary>ACCESS procedure permission mask.</summary>
[Flags]
public enum NfsAccessMode : uint
{
    None = 0,
    Read = 0x0001,
    Lookup = 0x0002,
    Modify = 0x0004,
    Extend = 0x0008,
    Delete = 0x0010,
    Execute = 0x0020
}

/// <summary>File or directory attributes returned by NFSv3.</summary>
public sealed record NfsFattr(NfsType Type, long Size, DateTime? Mtime)
{
    public uint Mode { get; init; }
    public uint LinkCount { get; init; }
    public uint Uid { get; init; }
    public uint Gid { get; init; }
    public ulong Used { get; init; }
    public ulong FileSystemId { get; init; }
    public ulong FileId { get; init; }
    public DateTime? Atime { get; init; }
    public DateTime? Ctime { get; init; }
    public NfsTimestamp? CtimeTimestamp { get; init; }
}

/// <summary>READDIR entry.</summary>
public sealed record NfsEntry(string Name, ulong FileId);

/// <summary>READDIRPLUS entry with attributes and optional file handle.</summary>
public sealed record NfsEntryPlus(string Name, ulong FileId, NfsFattr? Attr, byte[]? Handle);

/// <summary>LOOKUP/CREATE/MKDIR result containing a file handle and post-operation attributes.</summary>
public sealed record NfsLookup(byte[] Handle, NfsFattr? Attr);

/// <summary>NFS export advertised by mountd.</summary>
public sealed record NfsExport(string Path, IReadOnlyList<string> Groups);

/// <summary>File system capacity and availability reported by FSSTAT.</summary>
public sealed record NfsFileSystemStat(
    ulong TotalBytes,
    ulong FreeBytes,
    ulong AvailableBytes,
    ulong TotalFiles,
    ulong FreeFiles,
    ulong AvailableFiles,
    TimeSpan InvariantUntil);

/// <summary>Server transfer preferences and feature flags reported by FSINFO.</summary>
public sealed record NfsFileSystemInfo
{
    public uint PreferredReadSize { get; init; }
    public uint MaxReadSize { get; init; }
    public uint ReadMultipleSize { get; init; }
    public uint PreferredWriteSize { get; init; }
    public uint MaxWriteSize { get; init; }
    public uint WriteMultipleSize { get; init; }
    public uint PreferredReaddirSize { get; init; }
    public ulong MaxFileSize { get; init; }
    public TimeSpan TimeDelta { get; init; }
    public uint Properties { get; init; }
}

/// <summary>POSIX path constraints reported by PATHCONF.</summary>
public sealed record NfsPathConf
{
    public uint LinkMax { get; init; }
    public uint NameMax { get; init; }
    public bool NoTrunc { get; init; }
    public bool ChownRestricted { get; init; }
    public bool CaseInsensitive { get; init; }
    public bool CasePreserving { get; init; }
}

/// <summary>NFS timestamp preserving raw seconds and nanoseconds precision.</summary>
public readonly record struct NfsTimestamp(uint Seconds, uint Nanoseconds)
{
    public DateTime ToDateTimeUtc() =>
        DateTimeOffset.FromUnixTimeSeconds(Seconds)
            .AddTicks(Nanoseconds / 100)
            .UtcDateTime;

    public static NfsTimestamp FromDateTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        var dto = new DateTimeOffset(utc);
        return new NfsTimestamp(
            checked((uint)dto.ToUnixTimeSeconds()),
            checked((uint)((dto.Ticks % TimeSpan.TicksPerSecond) * 100)));
    }
}

/// <summary>Optional attributes used by SETATTR/CREATE/MKDIR.</summary>
public sealed record NfsSetAttributes
{
    public uint? Mode { get; init; }
    public uint? Uid { get; init; }
    public uint? Gid { get; init; }
    public ulong? Size { get; init; }
    public DateTime? Atime { get; init; }
    public DateTime? Mtime { get; init; }

    public static NfsSetAttributes FileDefault { get; } = new() { Mode = 0x1A4 };      // 0644
    public static NfsSetAttributes DirectoryDefault { get; } = new() { Mode = 0x1ED }; // 0755
}

/// <summary>Connection and protocol options for the NFS clients.</summary>
public sealed record NfsClientOptions
{
    public static NfsClientOptions Default { get; } = new();

    public uint UserId { get; init; }
    public uint GroupId { get; init; }
    public IReadOnlyList<uint> AuxiliaryGroups { get; init; } = Array.Empty<uint>();
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool UsePrivilegedSourcePort { get; init; } = true;
    public int PortmapPort { get; init; } = 111;
    public int MaxReadSize { get; init; } = 128 * 1024;
    public int MaxWriteSize { get; init; } = 128 * 1024;
    public int ReaddirCount { get; init; } = 32 * 1024;
    public NfsWriteStableHow StableHow { get; init; } = NfsWriteStableHow.FileSync;
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public bool EnableDirectoryCache { get; init; }
    public TimeSpan DirectoryCacheTtl { get; init; } = TimeSpan.FromSeconds(30);
    public bool TcpKeepAlive { get; init; } = true;
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
    public bool TcpNoDelay { get; init; } = true;
    public ILogger? Logger { get; init; }
    public IRpcSecGssMechanism? GssMechanism { get; init; }
    public RpcSecGssService GssService { get; init; } = RpcSecGssService.Integrity;
    public GssCredentials? GssCredentials { get; init; }
    public string? GssTargetName { get; init; }

    public void Validate()
    {
        if (AuxiliaryGroups is null)
            throw new NfsException("AuxiliaryGroups cannot be null.");
        if (PortmapPort is <= 0 or > 65535)
            throw new NfsException($"Invalid portmap port: {PortmapPort}.");
        if (CommandTimeout < TimeSpan.Zero)
            throw new NfsException("CommandTimeout cannot be negative.");
        if (MaxReadSize <= 0)
            throw new NfsException("MaxReadSize must be greater than zero.");
        if (MaxWriteSize <= 0)
            throw new NfsException("MaxWriteSize must be greater than zero.");
        if (ReaddirCount <= 0)
            throw new NfsException("ReaddirCount must be greater than zero.");
        if (!Enum.IsDefined(typeof(NfsWriteStableHow), StableHow))
            throw new NfsException($"Invalid write stability mode: {StableHow}.");
        if (MaxRetries < 0)
            throw new NfsException("MaxRetries cannot be negative.");
        if (RetryDelay < TimeSpan.Zero)
            throw new NfsException("RetryDelay cannot be negative.");
        if (EnableDirectoryCache && DirectoryCacheTtl <= TimeSpan.Zero)
            throw new NfsException("DirectoryCacheTtl must be greater than zero when directory caching is enabled.");
        if (TcpKeepAlive && KeepAliveInterval < TimeSpan.Zero)
            throw new NfsException("KeepAliveInterval cannot be negative.");
        if (AuxiliaryGroups.Count > 16)
            throw new NfsException("AUTH_SYS supports at most 16 auxiliary groups.");
    }
}

/// <summary>Result of ACCESS procedure — granted access mask.</summary>
public sealed record NfsAccessResult(NfsAccessMode Granted);

/// <summary>Result of WRITE procedure — count, committed stability, and write verifier.</summary>
public sealed record NfsWriteResult
{
    private readonly byte[] _writeVerifier;

    public NfsWriteResult(int count, NfsWriteStableHow committed, byte[] writeVerifier)
    {
        if (count < 0)
            throw new NfsException("WRITE count cannot be negative.");
        if (!Enum.IsDefined(typeof(NfsWriteStableHow), committed))
            throw new NfsException($"Invalid committed write stability mode: {committed}.");
        ArgumentNullException.ThrowIfNull(writeVerifier);
        if (writeVerifier.Length is not (0 or 8))
            throw new NfsException("WRITE verifier must be empty for local no-op results or exactly 8 bytes.");

        Count = count;
        Committed = committed;
        _writeVerifier = writeVerifier.ToArray();
    }

    public int Count { get; }

    public NfsWriteStableHow Committed { get; }

    public byte[] WriteVerifier => _writeVerifier.ToArray();
}

/// <summary>Result of COMMIT procedure — write verifier.</summary>
public sealed record NfsCommitResult
{
    private readonly byte[] _writeVerifier;

    public NfsCommitResult(byte[] writeVerifier)
    {
        ArgumentNullException.ThrowIfNull(writeVerifier);
        if (writeVerifier.Length != 8)
            throw new NfsException("COMMIT verifier must be exactly 8 bytes.");

        _writeVerifier = writeVerifier.ToArray();
    }

    public byte[] WriteVerifier => _writeVerifier.ToArray();
}

/// <summary>NFS protocol exception with optional nfsstat3/mountstat3 status.</summary>
public sealed class NfsException : Exception
{
    public uint? Status { get; }

    public NfsException(string message) : base(message) { }

    public NfsException(string message, Exception inner) : base(message, inner) { }

    public NfsException(string message, uint status) : base(message)
    {
        Status = status;
    }

    public bool IsNotFound => Status == NfsV3Status.NoEnt;
}

public static class NfsV3Status
{
    public const uint Ok = 0;
    public const uint Perm = 1;
    public const uint NoEnt = 2;
    public const uint Io = 5;
    public const uint Nxio = 6;
    public const uint Access = 13;
    public const uint Exist = 17;
    public const uint Xdev = 18;
    public const uint NotDir = 20;
    public const uint IsDir = 21;
    public const uint Inval = 22;
    public const uint Fbig = 27;
    public const uint NoSpc = 28;
    public const uint RoFs = 30;
    public const uint Mlink = 31;
    public const uint NamTooLong = 63;
    public const uint NotEmpty = 66;
    public const uint Dquot = 69;
    public const uint Stale = 70;
    public const uint Remote = 71;
    public const uint BadHandle = 10001;
    public const uint NotSync = 10002;
    public const uint BadCookie = 10003;
    public const uint NotSupp = 10004;
    public const uint TooSmall = 10005;
    public const uint ServerFault = 10006;
    public const uint BadType = 10007;
    public const uint Jukebox = 10008;

    public static string Describe(uint status) => status switch
    {
        Ok => "OK",
        Perm => "PERM",
        NoEnt => "NOENT",
        Io => "IO",
        Nxio => "NXIO",
        Access => "ACCESS",
        Exist => "EXIST",
        Xdev => "XDEV",
        NotDir => "NOTDIR",
        IsDir => "ISDIR",
        Inval => "INVAL",
        Fbig => "FBIG",
        NoSpc => "NOSPC",
        RoFs => "ROFS",
        Mlink => "MLINK",
        NamTooLong => "NAMETOOLONG",
        NotEmpty => "NOTEMPTY",
        Dquot => "DQUOT",
        Stale => "STALE",
        Remote => "REMOTE",
        BadHandle => "BADHANDLE",
        NotSync => "NOT_SYNC",
        BadCookie => "BAD_COOKIE",
        NotSupp => "NOTSUPP",
        TooSmall => "TOOSMALL",
        ServerFault => "SERVERFAULT",
        BadType => "BADTYPE",
        Jukebox => "JUKEBOX",
        _ => status.ToString()
    };
}
