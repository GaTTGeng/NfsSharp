namespace NfsSharp.Protocol;

/// <summary>Decoded ONC RPC reply header and the remaining procedure result payload.</summary>
public sealed class RpcReply
{
    internal RpcReply(uint verifierFlavor, byte[] verifier, XdrReader body)
    {
        VerifierFlavor = verifierFlavor;
        Verifier = verifier;
        Body = body;
    }

    /// <summary>Authentication flavor of the reply verifier.</summary>
    public uint VerifierFlavor { get; }

    /// <summary>Reply verifier bytes.</summary>
    public byte[] Verifier { get; }

    /// <summary>Reader positioned at the procedure-specific result payload.</summary>
    public XdrReader Body { get; }
}

/// <summary>Decodes and validates ONC RPC reply envelopes.</summary>
public static class RpcReplyParser
{
    private const uint Reply = 1;
    private const uint MsgAccepted = 0;
    private const uint MsgDenied = 1;
    private const uint Success = 0;
    private const uint ProgUnavail = 1;
    private const uint ProgMismatch = 2;
    private const uint ProcUnavail = 3;
    private const uint GarbageArgs = 4;
    private const uint SystemErr = 5;
    private const uint RpcMismatch = 0;
    private const uint AuthError = 1;
    private const uint AuthNone = 0;
    private const int MaxAuthBodyLength = 400;

    /// <summary>
    /// Decodes an ONC RPC reply for <paramref name="expectedXid"/> and returns a reader at its procedure result.
    /// RPC-level failures throw <see cref="NfsException"/> before a procedure result is exposed.
    /// </summary>
    public static RpcReply Decode(byte[] message, uint expectedXid)
    {
        var reader = new XdrReader(message);
        var xid = reader.UInt();
        if (xid != expectedXid)
            throw new NfsException($"RPC xid mismatch. Expected {expectedXid}, got {xid}.");

        var messageType = reader.UInt();
        if (messageType != Reply)
            throw new NfsException($"Unexpected RPC message type: {messageType}.");

        return reader.UInt() switch
        {
            MsgAccepted => DecodeAccepted(reader),
            MsgDenied => DecodeDenied(reader),
            var replyStat => throw new NfsException($"Invalid RPC reply_stat discriminator: {replyStat}.")
        };
    }

    private static RpcReply DecodeAccepted(XdrReader reader)
    {
        var verifierFlavor = reader.UInt();
        var verifier = reader.Opaque(MaxAuthBodyLength);
        if (verifierFlavor == AuthNone && verifier.Length != 0)
            throw new NfsException("Malformed RPC reply verifier: AUTH_NONE must be empty.");

        switch (reader.UInt())
        {
            case Success:
                return new RpcReply(verifierFlavor, verifier, reader);
            case ProgUnavail:
                throw new NfsException("RPC call rejected: program unavailable.");
            case ProgMismatch:
                ThrowProgramMismatch(reader, "RPC call rejected: program version mismatch");
                break;
            case ProcUnavail:
                throw new NfsException("RPC call rejected: procedure unavailable.");
            case GarbageArgs:
                throw new NfsException("RPC call rejected: server reported garbage arguments.");
            case SystemErr:
                throw new NfsException("RPC call rejected: server system error.");
            default:
                throw new NfsException("Invalid RPC accept_stat discriminator.");
        }

        throw new InvalidOperationException("Unreachable RPC reply state.");
    }

    private static RpcReply DecodeDenied(XdrReader reader)
    {
        switch (reader.UInt())
        {
            case RpcMismatch:
                ThrowProgramMismatch(reader, "RPC message denied: RPC version mismatch");
                break;
            case AuthError:
                var authStatus = reader.UInt();
                if (authStatus is < 1 or > 14)
                    throw new NfsException($"Invalid RPC auth_stat discriminator: {authStatus}.");
                throw new NfsException($"RPC message denied: authentication error ({DescribeAuthStatus(authStatus)}; auth_stat={authStatus}).");
            default:
                throw new NfsException("Invalid RPC reject_stat discriminator.");
        }

        throw new InvalidOperationException("Unreachable RPC reply state.");
    }

    private static void ThrowProgramMismatch(XdrReader reader, string prefix)
    {
        var low = reader.UInt();
        var high = reader.UInt();
        throw new NfsException($"{prefix} (supported range {low}..{high}).");
    }

    private static string DescribeAuthStatus(uint status) => status switch
    {
        1 => "bad credentials",
        2 => "rejected credentials",
        3 => "bad verifier",
        4 => "rejected verifier",
        5 => "credentials too weak",
        6 => "invalid response verifier",
        7 => "authentication failed",
        8 => "Kerberos error",
        9 => "ticket expired",
        10 => "ticket file error",
        11 => "credential decode error",
        12 => "network address mismatch",
        13 => "RPCSEC_GSS credential problem",
        14 => "RPCSEC_GSS context problem",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
