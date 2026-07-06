using System.Buffers.Binary;

namespace NfsSharp.Protocol;

/// <summary>NFSv4 operation opcodes (RFC 7530).</summary>
public enum NfsV4Op : uint
{
    Access = 3,
    Close = 4,
    Commit = 5,
    Create = 6,
    DelegPurge = 7,
    DelegReturn = 8,
    GetAttr = 9,
    GetFh = 10,
    Link = 11,
    Lock = 12,
    Lockt = 13,
    Locku = 14,
    Lookup = 15,
    LookupP = 16,
    NVerify = 17,
    Open = 18,
    OpenAttr = 19,
    OpenConfirm = 20,
    OpenDowngrade = 21,
    Putfh = 22,
    PutPubFh = 23,
    PutRootFh = 24,
    Read = 25,
    ReadDir = 26,
    ReadLink = 27,
    Remove = 28,
    Rename = 29,
    Renew = 30,
    RestoreFh = 31,
    SaveFh = 32,
    SecInfo = 33,
    SetAttr = 34,
    SetClientId = 35,
    SetClientIdConfirm = 36,
    Verify = 37,
    Write = 38,
    ReleaseLockOwner = 39,
    // NFSv4.1
    BackchannelCtl = 40,
    BindConnToSession = 41,
    ExchangeId = 42,
    CreateSession = 43,
    DestroySession = 44,
    FreeStateId = 45,
    GetDirDelegation = 46,
    Getdeviceinfo = 47,
    Getdevicelist = 48,
    LayoutCommit = 49,
    LayoutGet = 50,
    LayoutReturn = 51,
    SecInfoNoName = 52,
    Sequence = 53,
    SetSsv = 54,
    TestStateId = 55,
    WantDelegation = 56,
    DestroyClientId = 57,
    ReclaimComplete = 58,
    // NFSv4.2
    Allocate = 59,
    Copy = 60,
    CopyNotify = 61,
    Deallocate = 62,
    IoAdvise = 63,
    LayoutError = 64,
    LayoutStats = 65,
    OffloadCancel = 66,
    OffloadStatus = 67,
    ReadPlus = 68,
    Seek = 69,
    WriteSame = 70,
    Illegal = 10044,
}

/// <summary>NFSv4 status codes (nfsstat4).</summary>
public static class NfsV4Status
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
    public const uint Fbig = 27;
    public const uint NoSpc = 28;
    public const uint RoFs = 30;
    public const uint Mlink = 31;
    public const uint FhExpired = 10014;
    public const uint ShareDenied = 10015;
    public const uint WrongType = 10016;
    public const uint FhReval = 10017;
    public const uint RestoreFh = 10018;
    public const uint BadStateId = 10022;
    public const uint StaleStateId = 10023;
    public const uint BadSeqId = 10026;
    public const uint LockNotSupp = 10028;
    public const uint LockRange = 10029;
    public const uint Grace = 10013;
    public const uint AttrNotSupp = 10032;
    public const uint NoGrace = 10033;
    public const uint ReclaimBad = 10034;
    public const uint ReclaimConflict = 10035;
    public const uint BadXdr = 10036;
    public const uint OpenMode = 10038;
    public const uint ReplayMe = 10044;
    public const uint Delay = 10068;

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
        Fbig => "FBIG",
        NoSpc => "NOSPC",
        RoFs => "ROFS",
        Mlink => "MLINK",
        FhExpired => "FHEXPIRED",
        ShareDenied => "SHAREDENIED",
        WrongType => "WRONGTYPE",
        FhReval => "FHREVAL",
        RestoreFh => "RESTOREFH",
        BadStateId => "BADSTATEID",
        StaleStateId => "EXPIRED",
        BadSeqId => "BAD_SEQID",
        LockNotSupp => "LOCK_NOTSUPP",
        OpenMode => "OPENMODE",
        ReplayMe => "REPLAY_ME",
        BadXdr => "BAD_XDR",
        LockRange => "LOCK_RANGE",
        Grace => "GRACE",
        NoGrace => "NO_GRACE",
        ReclaimBad => "RECLAIM_BAD",
        ReclaimConflict => "RECLAIM_CONFLICT",
        Delay => "DELAY",
        _ => status.ToString()
    };
}

/// <summary>NFSv4 file types.</summary>
public enum NfsV4FType : uint
{
    Reg = 1,
    Dir = 2,
    Blk = 3,
    Chr = 4,
    Lnk = 5,
    Sock = 6,
    Fifo = 7,
    AttrDir = 8,
    NamedAttr = 9,
}

/// <summary>NFSv4 bitmap for attribute numbers.</summary>
public sealed class NfsV4Bitmap
{
    private readonly uint[] _masks;

    public NfsV4Bitmap(params uint[] masks)
    {
        ArgumentNullException.ThrowIfNull(masks);
        _masks = masks.ToArray();
    }

    public bool HasAttr(uint attrNum)
    {
        var word = attrNum / 32;
        var bit = attrNum % 32;
        return word < _masks.Length && (_masks[word] & (1u << (int)bit)) != 0;
    }

    public uint[] Masks => _masks.ToArray();

    public void Encode(XdrWriter writer)
    {
        writer.UInt((uint)_masks.Length);
        foreach (var mask in _masks)
            writer.UInt(mask);
    }

    public static NfsV4Bitmap Decode(XdrReader reader)
    {
        var count = (int)reader.UInt();
        var masks = new uint[count];
        for (var i = 0; i < count; i++)
            masks[i] = reader.UInt();
        return new NfsV4Bitmap(masks);
    }

    public static NfsV4Bitmap Of(params uint[] attrs)
    {
        ArgumentNullException.ThrowIfNull(attrs);
        if (attrs.Length == 0)
            return new NfsV4Bitmap();

        var masks = new uint[checked((int)(attrs.Max() / 32) + 1)];
        foreach (var attr in attrs)
        {
            var word = checked((int)(attr / 32));
            var bit = attr % 32;
            masks[word] |= 1u << (int)bit;
        }

        return new NfsV4Bitmap(masks);
    }
}

/// <summary>NFSv4 common attribute numbers (RFC 7530 §5.6).</summary>
public static class NfsV4Attr
{
    public const uint SupportedAttrs = 0;
    public const uint Type = 1;
    public const uint FhExpireType = 2;
    public const uint Change = 3;
    public const uint Size = 4;
    public const uint LinkSupport = 5;
    public const uint SymlinkSupport = 6;
    public const uint NamedAttr = 7;
    public const uint Fsid = 8;
    public const uint UniqueHandles = 9;
    public const uint LeaseTime = 10;
    public const uint RdattrError = 11;
    public const uint Acl = 12;
    public const uint Aclsupport = 13;
    public const uint Archive = 14;
    public const uint Cansettime = 15;
    public const uint CaseInsensitive = 16;
    public const uint CasePreserving = 17;
    public const uint ChownRestricted = 18;
    public const uint Fileid = 19;
    public const uint FilesAvail = 20;
    public const uint FilesFree = 21;
    public const uint FilesTotal = 22;
    public const uint FsLocations = 24;
    public const uint Hidden = 25;
    public const uint Homogeneous = 26;
    public const uint Maxfilesize = 27;
    public const uint Maxlink = 28;
    public const uint Maxname = 29;
    public const uint Maxread = 30;
    public const uint Maxwrite = 31;
    public const uint Mimetype = 32;
    public const uint Mode = 33;
    public const uint NoTrunc = 34;
    public const uint Numinlinks = 35;
    public const uint Owner = 36;
    public const uint OwnerGroup = 37;
    public const uint QuotaAvailHard = 38;
    public const uint QuotaAvailSoft = 39;
    public const uint QuotaUsed = 40;
    public const uint Rawdev = 41;
    public const uint SpaceAvail = 42;
    public const uint SpaceFree = 43;
    public const uint SpaceTotal = 44;
    public const uint SpaceUsed = 45;
    public const uint TimeAccess = 47;
    public const uint TimeBackup = 48;
    public const uint TimeCreate = 49;
    public const uint TimeDelta = 50;
    public const uint TimeMetadata = 51;
    public const uint TimeModify = 52;
    public const uint MountedOnFileid = 55;
    public const uint DirNotifDelay = 56;
    public const uint DirentNotifDelay = 57;
    public const uint Dacl = 58;
    public const uint Sacl = 59;
    public const uint ChangePolicy = 60;
    public const uint FsStatus = 61;
    // NFSv4.1
    public const uint FhPageSize = 62;
    public const uint PnfsLayoutType = 63;
    public const uint LayoutAlignment = 64;
    public const uint LayoutBlksize = 65;
    public const uint LayoutHint = 66;
    public const uint LayoutTypes = 67;
    public const uint Mdsthreshold = 68;
    public const uint RetentionGet = 69;
    public const uint RetentionSet = 70;
    public const uint RetentevtGet = 71;
    public const uint RetentevtSet = 72;
    public const uint RetentionHold = 73;
    public const uint ModeSetMasked = 74;
    // NFSv4.2
    public const uint XattrSupport = 75;
    public const uint SeekHoleData = 77;
}

/// <summary>NFSv4 COMPOUND request arguments.</summary>
public sealed class NfsV4CompoundRequest
{
    public string Tag { get; set; } = "";
    public uint MinorVersion { get; set; }
    public List<NfsV4Operation> Operations { get; set; } = new();
}

/// <summary>NFSv4 COMPOUND response.</summary>
public sealed class NfsV4CompoundResponse
{
    public string Tag { get; set; } = "";
    public uint Status { get; set; }
    public List<NfsV4OperationResult> Results { get; set; } = new();
}

/// <summary>A single NFSv4 operation in a COMPOUND request.</summary>
public sealed class NfsV4Operation
{
    public NfsV4Op Op { get; set; }
    public byte[]? Args { get; set; }
}

/// <summary>A single NFSv4 operation result in a COMPOUND response.</summary>
public sealed class NfsV4OperationResult
{
    public NfsV4Op Op { get; set; }
    public uint Status { get; set; }
    public XdrReader? Data { get; set; }
}

/// <summary>NFSv4 attributes decoded from GETATTR response.</summary>
public sealed record NfsV4Fattr
{
    public NfsV4FType Type { get; init; }
    public ulong Change { get; init; }
    public ulong Size { get; init; }
    public uint LinkSupport { get; init; }
    public uint SymlinkSupport { get; init; }
    public ulong Fileid { get; init; }
    public uint Mode { get; init; }
    public uint Numinlinks { get; init; }
    public string Owner { get; init; } = "";
    public string OwnerGroup { get; init; } = "";
    public ulong SpaceAvail { get; init; }
    public ulong SpaceFree { get; init; }
    public ulong SpaceTotal { get; init; }
    public ulong SpaceUsed { get; init; }
    public ulong Maxfilesize { get; init; }
    public uint Maxread { get; init; }
    public uint Maxwrite { get; init; }
    public uint Maxname { get; init; }
    public uint Maxlink { get; init; }
    public DateTime? TimeAccess { get; init; }
    public DateTime? TimeModify { get; init; }
    public DateTime? TimeMetadata { get; init; }
    public uint LeaseTime { get; init; }
    public bool CaseInsensitive { get; init; }
    public bool CasePreserving { get; init; }
    public bool NoTrunc { get; init; }
    public bool ChownRestricted { get; init; }
    public uint[]? SupportedAttrs { get; init; }
    public uint Aclsupport { get; init; }
}

/// <summary>NFSv4 OPEN arguments.</summary>
public enum NfsV4OpenShareAccess : uint
{
    Read = 1,
    Write = 2,
    Both = 3,
}

[Flags]
public enum NfsV4OpenShareDeny : uint
{
    None = 0,
    Read = 1,
    Write = 2,
    Both = 3,
}

public enum NfsV4OpenClaimType : uint
{
    Null = 0,
    Previous = 1,
    DelegateCur = 2,
    DelegatePrev = 3,
}

public enum NfsV4CreateMode : uint
{
    Unchecked = 0,
    Guarded = 1,
    Exclusive = 2,
}

/// <summary>NFSv4 stateid — 128-bit opaque identifier for file state.</summary>
public sealed class NfsV4StateId
{
    public static readonly NfsV4StateId Zero = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    public static readonly NfsV4StateId Anonymous = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    public static readonly NfsV4StateId Special = new(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    private readonly byte[] _data;

    public byte[] Data => _data.ToArray();

    public NfsV4StateId(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != 16)
            throw new ArgumentException("StateId must be exactly 16 bytes.");
        _data = data.ToArray();
    }

    public void Encode(XdrWriter writer)
    {
        writer.UInt(BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4)));
        writer.FixedBytes(_data.AsSpan(4, 12));
    }

    public static NfsV4StateId Decode(XdrReader reader)
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), reader.UInt());
        reader.FixedBytes(12).CopyTo(data.AsSpan(4, 12));
        return new NfsV4StateId(data);
    }
}

/// <summary>NFSv4 OPEN result.</summary>
public sealed class NfsV4OpenResult
{
    public uint Status { get; init; }
    public NfsV4StateId? StateId { get; init; }
    public byte[]? FileHandle { get; init; }
    public NfsV4Fattr? Attributes { get; init; }
    public bool Delegation { get; init; }
}

/// <summary>NFSv4 READDIR entry.</summary>
public sealed class NfsV4DirEntry
{
    public ulong Cookie { get; init; }
    public string Name { get; init; } = "";
    public NfsV4Fattr? Attributes { get; init; }
}

/// <summary>NFSv4 session info for v4.1+.</summary>
public sealed class NfsV4SessionInfo
{
    public ulong ClientId { get; init; }
    public ulong SequenceId { get; init; }
    public uint SessionId { get; init; }
    public byte[] SessionIdBytes { get; init; } = Array.Empty<byte>();
    public uint[] ChannelAttributes { get; init; } = Array.Empty<uint>();
    public uint LeaseTime { get; init; }
}
