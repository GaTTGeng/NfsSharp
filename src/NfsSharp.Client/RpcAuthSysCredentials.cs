using System.Net;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal static class RpcAuthSysCredentials
{
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

        return RpcAuthSys.Encode(
            0,
            machineName,
            options.UserId,
            options.GroupId,
            options.AuxiliaryGroups ?? Array.Empty<uint>());
    }
}
