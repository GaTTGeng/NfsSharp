namespace NfsSharp.Protocol;

/// <summary>Decoded ONC RPC reply header and the remaining procedure result payload.</summary>
public sealed class RpcReply
{
    internal RpcReply(uint verifierFlavor, byte[] verifier, XdrReader body, uint acceptStatus, string? acceptFailureMessage)
    {
        VerifierFlavor = verifierFlavor;
        Verifier = verifier;
        Body = body;
        AcceptStatus = acceptStatus;
        AcceptFailureMessage = acceptFailureMessage;
    }

    /// <summary>Authentication flavor of the reply verifier.</summary>
    public uint VerifierFlavor { get; }

    /// <summary>Reply verifier bytes.</summary>
    public byte[] Verifier { get; }

    /// <summary>Reader positioned at the procedure-specific result payload.</summary>
    public XdrReader Body { get; }

    /// <summary>ONC RPC accept_stat discriminator (0 = SUCCESS).</summary>
    public uint AcceptStatus { get; }

    /// <summary>Readable failure text when <see cref="AcceptStatus"/> is not SUCCESS.</summary>
    public string? AcceptFailureMessage { get; }

    /// <summary>Whether the server accepted the call (accept_stat = SUCCESS).</summary>
    public bool IsSuccess => AcceptStatus == 0;
}

/// <summary>Decodes and validates ONC RPC reply envelopes (RFC 5531).</summary>
public static class RpcReplyParser
{
    // reply_stat / accept_stat / reject_stat / auth_stat discriminators (RFC 5531).
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
    // RFC 5531 caps opaque_auth bodies at 400 bytes.
    private const int MaxAuthBodyLength = 400;

    /// <summary>
    /// Decodes an ONC RPC reply for <paramref name="expectedXid"/> and returns a reader at its procedure result.
    /// RPC-level failures throw <see cref="NfsException"/> before a procedure result is exposed.
    /// </summary>
    public static RpcReply Decode(byte[] message, uint expectedXid)
    {
        var reply = DecodeAccepted(message, expectedXid);
        ThrowIfAcceptFailed(reply);
        return reply;
    }

    /// <summary>
    /// Decodes an accepted ONC RPC reply for <paramref name="expectedXid"/>, preserving the reply
    /// verifier even when accept_stat is not SUCCESS so security layers can validate it first.
    /// Envelope-level failures (xid mismatch, MSG_DENIED, invalid discriminators, malformed XDR) still throw.
    /// </summary>
    public static RpcReply DecodeAccepted(byte[] message, uint expectedXid)
    {
        var reader = new XdrReader(message);
        // Match the reply to the outstanding call before interpreting anything else.
        var xid = reader.UInt();
        if (xid != expectedXid)
            throw new NfsException($"RPC xid mismatch. Expected {expectedXid}, got {xid}.");

        // Only REPLY messages are legal here; a CALL would mean a mis-framed stream.
        var messageType = reader.UInt();
        if (messageType != Reply)
            throw new NfsException($"Unexpected RPC message type: {messageType}.");

        // Branch on reply_stat: MSG_ACCEPTED continues into accept_stat, MSG_DENIED into reject_stat.
        return reader.UInt() switch
        {
            MsgAccepted => DecodeAcceptedBody(reader),
            MsgDenied => DecodeDenied(reader),
            var replyStat => throw new NfsException($"Invalid RPC reply_stat discriminator: {replyStat}.")
        };
    }

    /// <summary>Throws when an accepted reply did not have accept_stat SUCCESS.</summary>
    public static void ThrowIfAcceptFailed(RpcReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.IsSuccess)
            throw new NfsException(reply.AcceptFailureMessage ?? "RPC call rejected by the server.");
    }

    /// <summary>Decodes MSG_ACCEPTED: reply verifier, then accept_stat; every accept arm keeps the verifier.</summary>
    private static RpcReply DecodeAcceptedBody(XdrReader reader)
    {
        // Consume the reply verifier first; every MSG_ACCEPTED carries it regardless of accept_stat.
        var verifierFlavor = reader.UInt();
        var verifier = reader.Opaque(MaxAuthBodyLength);
        // AUTH_NONE must carry a zero-length verifier body; anything else is malformed.
        if (verifierFlavor == AuthNone && verifier.Length != 0)
            throw new NfsException("Malformed RPC reply verifier: AUTH_NONE must be empty.");

        var acceptStatus = reader.UInt();
        return acceptStatus switch
        {
            Success =>
                new RpcReply(verifierFlavor, verifier, reader, Success, null),
            ProgUnavail =>
                Failed(verifierFlavor, verifier, reader, acceptStatus, "RPC call rejected: program unavailable."),
            ProgMismatch =>
                FailedProgramMismatch(verifierFlavor, verifier, reader, acceptStatus, "RPC call rejected: program version mismatch"),
            ProcUnavail =>
                Failed(verifierFlavor, verifier, reader, acceptStatus, "RPC call rejected: procedure unavailable."),
            GarbageArgs =>
                Failed(verifierFlavor, verifier, reader, acceptStatus, "RPC call rejected: server reported garbage arguments."),
            SystemErr =>
                Failed(verifierFlavor, verifier, reader, acceptStatus, "RPC call rejected: server system error."),
            _ => throw new NfsException("Invalid RPC accept_stat discriminator.")
        };
    }

    private static RpcReply Failed(
        uint verifierFlavor,
        byte[] verifier,
        XdrReader reader,
        uint acceptStatus,
        string message) =>
        new(verifierFlavor, verifier, reader, acceptStatus, message);

    /// <summary>Builds a mismatch failure reply that still carries the low/high version range in the body.</summary>
    private static RpcReply FailedProgramMismatch(
        uint verifierFlavor,
        byte[] verifier,
        XdrReader reader,
        uint acceptStatus,
        string prefix)
    {
        // Both mismatch forms end with the supported version range before the failure surfaces.
        var low = reader.UInt();
        var high = reader.UInt();
        return new RpcReply(
            verifierFlavor,
            verifier,
            reader,
            acceptStatus,
            $"{prefix} (supported range {low}..{high}).");
    }

    /// <summary>Decodes MSG_DENIED: RPC version mismatch or an auth_stat authentication failure.</summary>
    private static RpcReply DecodeDenied(XdrReader reader)
    {
        // reject_stat selects the payload: RPC_MISMATCH carries versions, AUTH_ERROR carries auth_stat.
        switch (reader.UInt())
        {
            case RpcMismatch:
                ThrowProgramMismatch(reader, "RPC message denied: RPC version mismatch");
                break;
            case AuthError:
                var authStatus = reader.UInt();
                // auth_stat is a closed 1..14 range in RFC 5531; anything else is not mappable.
                if (authStatus is < 1 or > 14)
                    throw new NfsException($"Invalid RPC auth_stat discriminator: {authStatus}.");
                throw new NfsException($"RPC message denied: authentication error ({DescribeAuthStatus(authStatus)}; auth_stat={authStatus}).");
            default:
                throw new NfsException("Invalid RPC reject_stat discriminator.");
        }

        throw new InvalidOperationException("Unreachable RPC reply state.");
    }

    /// <summary>Throws with the low/high version range carried by a mismatch reply.</summary>
    private static void ThrowProgramMismatch(XdrReader reader, string prefix)
    {
        // Both mismatch forms end with the supported version range before the failure surfaces.
        var low = reader.UInt();
        var high = reader.UInt();
        throw new NfsException($"{prefix} (supported range {low}..{high}).");
    }

    /// <summary>Maps RFC 5531 auth_stat values to readable names.</summary>
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
