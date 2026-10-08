using System.Net;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Builds the AUTH_SYS credential body carried on each CALL (RFC 5531).</summary>
internal static class RpcAuthSysCredentials
{
    /// <summary>Encode stamp, machine name, uid, gid, and auxiliary groups for AUTH_SYS.</summary>
    internal static byte[] Encode(NfsClientOptions options)
    {
        string machineName;
        try
        {
            machineName = Dns.GetHostName();
        }
        catch
        {
            machineName = "nfssharp";
        }

        // The AUTH_SYS stamp field is unused by NFS servers and is sent as 0.
        return RpcAuthSys.Encode(
            0,
            machineName,
            options.UserId,
            options.GroupId,
            options.AuxiliaryGroups ?? Array.Empty<uint>());
    }
}
