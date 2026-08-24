namespace NfsSharp.Client;

internal static class NfsRpcConstants
{
    internal const uint ProgPortmap = 100000;
    internal const uint VerPortmap = 2;
    internal const uint PmapGetPort = 3;

    internal const uint ProgMount = 100005;
    internal const uint VerMount = 3;
    internal const uint MountMnt = 1;
    internal const uint MountUmnt = 3;
    internal const uint MountExport = 5;

    internal const uint ProgNfs = 100003;
    internal const uint VerNfs = 3;
    internal const uint NfsGetAttr = 1;
    internal const uint NfsSetAttr = 2;
    internal const uint NfsLookup = 3;
    internal const uint NfsAccess = 4;
    internal const uint NfsReadlink = 5;
    internal const uint NfsRead = 6;
    internal const uint NfsWrite = 7;
    internal const uint NfsCreate = 8;
    internal const uint NfsMkdir = 9;
    internal const uint NfsSymlink = 10;
    internal const uint NfsMknod = 11;
    internal const uint NfsRemove = 12;
    internal const uint NfsRmdir = 13;
    internal const uint NfsRename = 14;
    internal const uint NfsLink = 15;
    internal const uint NfsReadDir = 16;
    internal const uint NfsReadDirPlus = 17;
    internal const uint NfsFsstat = 18;
    internal const uint NfsFsinfo = 19;
    internal const uint NfsPathconf = 20;
    internal const uint NfsCommit = 21;
}
