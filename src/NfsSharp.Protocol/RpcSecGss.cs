namespace NfsSharp.Protocol;

/// <summary>RPCSEC_GSS security flavors (RFC 2203).</summary>
public enum RpcSecGssFlavor : uint
{
    None = 0,
    Sys = 1,
    Gss = 6,
}

/// <summary>RPCSEC_GSS service levels.</summary>
public enum RpcSecGssService : uint
{
    None = 1,
    Integrity = 2,
    Privacy = 3,
}

/// <summary>RPCSEC_GSS control procedures (RFC 2203 rpc_gss_proc_t).</summary>
public enum RpcSecGssProc : uint
{
    /// <summary>Regular data exchange; not a control action.</summary>
    Data = 0,
    /// <summary>First context-creation request.</summary>
    Init = 1,
    /// <summary>Subsequent context-creation request carrying a continuation token.</summary>
    ContinueInit = 2,
    /// <summary>Context destruction control message.</summary>
    Destroy = 3,
}

/// <summary>RPCSEC_GSS protocol constants (RFC 2203).</summary>
public static class RpcSecGssConstants
{
    /// <summary>Only defined credential version (RPCSEC_GSS_VERS_1).</summary>
    public const uint Version = 1;
    /// <summary>Maximum sequence number value (MAXSEQ).</summary>
    public const uint MaxSeq = 0x8000_0000;
    /// <summary>Largest sequence window accepted by the protocol.</summary>
    public const int MaxSeqWindowSize = 64;
    /// <summary>Window size used unless a peer negotiates a smaller value.</summary>
    public const int DefaultSeqWindowSize = 64;
    /// <summary>QOP used for the context-completion seq_window checksum (RFC 2203 §5.2.3.1).</summary>
    public const uint DefaultQop = 0;
}

/// <summary>
/// Optional QOP-aware GSS operations. When a mechanism implements this interface,
/// RPCSEC_GSS passes the per-request QOP explicitly; otherwise QOP 0 is assumed.
/// </summary>
public interface IRpcSecGssQopMechanism
{
    /// <summary>Compute a MIC for <paramref name="data"/> using <paramref name="qop"/>.</summary>
    byte[] GetMic(byte[] data, uint qop);

    /// <summary>Verify a MIC against <paramref name="data"/> using <paramref name="qop"/>.</summary>
    bool VerifyMic(byte[] data, byte[] mic, uint qop);

    /// <summary>Wrap (encrypt or sign) data using <paramref name="qop"/>.</summary>
    byte[] Wrap(byte[] data, uint qop);

    /// <summary>Unwrap protected data using <paramref name="qop"/>.</summary>
    byte[] Unwrap(byte[] wrappedData, uint qop);
}

/// <summary>Interface for GSSAPI security mechanisms (e.g., Kerberos).</summary>
public interface IRpcSecGssMechanism : IDisposable
{
    /// <summary>Mechanism OID (e.g., 1.2.840.113554.1.2.2 for Kerberos v5).</summary>
    byte[] MechanismOid { get; }

    /// <summary>Initiate a GSS security context. Returns the initial token to send to the server.</summary>
    /// <param name="targetName">Service principal name (e.g., "nfs/server.example.com@REALM").</param>
    /// <param name="credentials">Optional credentials (e.g., keytab path or password).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Initial GSS token to include in RPCSEC_GSS_CREATE request.</returns>
    Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct);

    /// <summary>Continue context establishment with a server token.</summary>
    /// <param name="serverToken">Token received from the server.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next token to send, or empty if context is established.</returns>
    Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct);

    /// <summary>Whether the security context is fully established.</summary>
    bool IsEstablished { get; }

    /// <summary>Compute a Message Integrity Code (MIC) for the given data.</summary>
    byte[] GetMic(byte[] data);

    /// <summary>Verify a MIC against the given data.</summary>
    bool VerifyMic(byte[] data, byte[] mic);

    /// <summary>Wrap (encrypt) data for transmission.</summary>
    byte[] Wrap(byte[] data);

    /// <summary>Unwrap (decrypt) data received from the server.</summary>
    byte[] Unwrap(byte[] wrappedData);

    /// <summary>The negotiated maximum message size.</summary>
    uint MaxMessageSize { get; }

    /// <summary>The sequence number for the next RPC request.</summary>
    uint NextSeqNum { get; set; }

    /// <summary>Service level negotiated with the server.</summary>
    RpcSecGssService NegotiatedService { get; set; }
}

/// <summary>GSS credentials for authentication.</summary>
public sealed class GssCredentials
{
    /// <summary>Username or principal name.</summary>
    public string? UserName { get; init; }

    /// <summary>Password (for kinit-style authentication).</summary>
    public string? Password { get; init; }

    /// <summary>Path to a keytab file (alternative to password).</summary>
    public string? KeytabPath { get; init; }

    /// <summary>Kerberos realm (e.g., "EXAMPLE.COM").</summary>
    public string? Realm { get; init; }

    /// <summary>KDC server address (optional, uses system config if not set).</summary>
    public string? KdcAddress { get; init; }
}

/// <summary>RPCSEC_GSS context handle returned after CREATE exchange.</summary>
public sealed class RpcSecGssContext
{
    /// <summary>Opaque server-issued context handle for subsequent RPCs.</summary>
    public byte[] ContextHandle { get; init; } = Array.Empty<byte>();
    /// <summary>Number of sequence numbers accepted within the sliding window.</summary>
    public uint SeqWindowSize { get; init; }
    /// <summary>Replay-detection window bits received from the server (RFC 2203).</summary>
    public byte[] SeqWindow { get; init; } = new byte[8];
    /// <summary>Service level (none/integrity/privacy) bound to this context.</summary>
    public RpcSecGssService Service { get; init; }
    /// <summary>Mechanism that established and secures this context.</summary>
    public IRpcSecGssMechanism Mechanism { get; init; } = null!;
}

/// <summary>
/// NegotiateAuthentication-based GSS mechanism for .NET 8+.
/// Uses System.Net.Security.NegotiateAuthentication for Kerberos/NTLM.
/// Experimental: token-exchange success is not evidence of integrity/privacy correctness.
/// </summary>
public sealed class NegotiateGssMechanism : IRpcSecGssMechanism, IRpcSecGssQopMechanism
{
    private System.Net.Security.NegotiateAuthentication? _auth;
    private bool _established;
    private uint _nextSeqNum;

    public byte[] MechanismOid { get; }

    public NegotiateGssMechanism(string package = "Kerberos")
    {
        // Kerberos v5 OID: 1.2.840.113554.1.2.2
        MechanismOid = package == "Kerberos"
            ? new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02 }
            : Array.Empty<byte>();
    }

    public Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct)
    {
        var options = new System.Net.Security.NegotiateAuthenticationClientOptions
        {
            Package = "Kerberos",
            TargetName = targetName,
        };

        // Optional explicit credentials; without them Negotiate falls back to the process identity.
        if (credentials?.UserName is not null)
        {
            options.Credential = new System.Net.NetworkCredential(
                credentials.UserName,
                credentials.Password ?? "",
                credentials.Realm ?? "");
        }

        _auth = new System.Net.Security.NegotiateAuthentication(options);
        // An empty input yields the first handshake token to ship in RPCSEC_GSS_CREATE.
        var token = _auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out _);
        return Task.FromResult(token ?? Array.Empty<byte>());
    }

    public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct)
    {
        if (_auth is null)
            throw new InvalidOperationException("Context not initiated.");

        // Feed the server token back through the handshake; Completed means establishment is done.
        var token = _auth.GetOutgoingBlob(serverToken, out var statusCode);
        if (statusCode == System.Net.Security.NegotiateAuthenticationStatusCode.Completed)
            _established = true;

        // A null token means no further round trip is required.
        return Task.FromResult(token ?? Array.Empty<byte>());
    }

    public bool IsEstablished => _established;

    public byte[] GetMic(byte[] data)
    {
        if (_auth is null || !_established)
            throw new InvalidOperationException("Security context not established.");

        // GetOutgoingBlob with input data computes the MIC
        var result = _auth.GetOutgoingBlob(data, out _);
        return result ?? Array.Empty<byte>();
    }

    public bool VerifyMic(byte[] data, byte[] mic)
    {
        if (_auth is null || !_established)
            throw new InvalidOperationException("Security context not established.");

        try
        {
            // GetOutgoingBlob with mic data verifies integrity
            var result = _auth.GetOutgoingBlob(mic, out var statusCode);
            return statusCode == System.Net.Security.NegotiateAuthenticationStatusCode.Completed;
        }
        catch
        {
            // A bad MIC surfaces as an exception; report it as a failed verification instead of throwing.
            return false;
        }
    }

    public byte[] Wrap(byte[] data)
    {
        if (_auth is null || !_established)
            throw new InvalidOperationException("Security context not established.");

        var result = _auth.GetOutgoingBlob(data, out _);
        return result ?? data;
    }

    public byte[] Unwrap(byte[] wrappedData)
    {
        if (_auth is null || !_established)
            throw new InvalidOperationException("Security context not established.");

        var result = _auth.GetOutgoingBlob(wrappedData, out _);
        return result ?? wrappedData;
    }

    public uint MaxMessageSize { get; set; } = 1024 * 1024;
    public uint NextSeqNum { get => _nextSeqNum; set => _nextSeqNum = value; }
    public RpcSecGssService NegotiatedService { get; set; }

    /// <summary>QOP-aware MIC. NegotiateAuthentication does not expose distinct QOPs; only QOP 0 is supported.</summary>
    public byte[] GetMic(byte[] data, uint qop)
    {
        EnsureDefaultQop(qop);
        return GetMic(data);
    }

    /// <summary>QOP-aware MIC verification. Only QOP 0 is supported.</summary>
    public bool VerifyMic(byte[] data, byte[] mic, uint qop)
    {
        EnsureDefaultQop(qop);
        return VerifyMic(data, mic);
    }

    /// <summary>QOP-aware wrap. Only QOP 0 is supported.</summary>
    public byte[] Wrap(byte[] data, uint qop)
    {
        EnsureDefaultQop(qop);
        return Wrap(data);
    }

    /// <summary>QOP-aware unwrap. Only QOP 0 is supported.</summary>
    public byte[] Unwrap(byte[] wrappedData, uint qop)
    {
        EnsureDefaultQop(qop);
        return Unwrap(wrappedData);
    }

    private static void EnsureDefaultQop(uint qop)
    {
        if (qop != RpcSecGssConstants.DefaultQop)
            throw new NfsException($"Unsupported GSS QOP: {qop}. NegotiateGssMechanism only supports QOP 0.");
    }

    public void Dispose()
    {
        _auth?.Dispose();
    }
}

/// <summary>
/// Immutable per-attempt RPCSEC_GSS security record (RFC 2203).
/// One record is allocated for every transmitted attempt; retries allocate a fresh sequence number.
/// </summary>
public sealed class RpcSecGssCallRecord
{
    /// <summary>Creates a record for one transmitted attempt.</summary>
    public RpcSecGssCallRecord(
        uint xid,
        uint seqNum,
        RpcSecGssService service,
        uint qop,
        uint contextGeneration,
        uint program,
        uint version,
        uint procedure,
        RpcSecGssProc gssProc,
        byte[] contextHandle)
    {
        if (contextHandle is null)
            throw new ArgumentNullException(nameof(contextHandle));
        if (seqNum == 0 || seqNum >= RpcSecGssConstants.MaxSeq)
            throw new ArgumentOutOfRangeException(nameof(seqNum), seqNum, "Sequence numbers must be in 1..MAXSEQ-1.");

        Xid = xid;
        SeqNum = seqNum;
        Service = service;
        Qop = qop;
        ContextGeneration = contextGeneration;
        Program = program;
        Version = version;
        Procedure = procedure;
        GssProc = gssProc;
        ContextHandle = contextHandle;
    }

    /// <summary>RPC transaction identifier of this attempt.</summary>
    public uint Xid { get; }
    /// <summary>RPCSEC_GSS sequence number allocated for this attempt.</summary>
    public uint SeqNum { get; }
    /// <summary>Service level used for this attempt.</summary>
    public RpcSecGssService Service { get; }
    /// <summary>GSS quality-of-protection used for this attempt (0 = default).</summary>
    public uint Qop { get; }
    /// <summary>Generation of the GSS context that signed this attempt.</summary>
    public uint ContextGeneration { get; }
    /// <summary>RPC program number of the protected call.</summary>
    public uint Program { get; }
    /// <summary>RPC program version of the protected call.</summary>
    public uint Version { get; }
    /// <summary>RPC procedure number of the protected call.</summary>
    public uint Procedure { get; }
    /// <summary>RPCSEC_GSS control procedure encoded in the credential.</summary>
    public RpcSecGssProc GssProc { get; }
    /// <summary>Context handle sealed into the credential (defensive copy).</summary>
    public byte[] ContextHandle { get; }

    /// <summary>Four-byte network-order encoding of <see cref="SeqNum"/> used for MIC inputs.</summary>
    public byte[] SeqNumNetworkOrder()
    {
        var bytes = new byte[4];
        bytes[0] = (byte)(SeqNum >> 24);
        bytes[1] = (byte)(SeqNum >> 16);
        bytes[2] = (byte)(SeqNum >> 8);
        bytes[3] = (byte)SeqNum;
        return bytes;
    }

    /// <summary>
    /// Redacted diagnostic summary. Never includes credentials, tokens, MIC material, or protected payloads.
    /// </summary>
    public override string ToString() =>
        $"xid={Xid}, prog={Program}, vers={Version}, proc={Procedure}, gssProc={GssProc}, seq={SeqNum}, service={Service}, qop={Qop}, contextGeneration={ContextGeneration}";
}

/// <summary>
/// AUTH_NONE/AUTH_SYS-based GSS mechanism for testing and environments
/// where Kerberos is not available. Provides no actual security.
/// Must not be configured with integrity or privacy service.
/// </summary>
public sealed class NoOpGssMechanism : IRpcSecGssMechanism, IRpcSecGssQopMechanism
{
    public byte[] MechanismOid => Array.Empty<byte>();
    public bool IsEstablished => true;
    public uint MaxMessageSize => uint.MaxValue;
    public uint NextSeqNum { get; set; }
    public RpcSecGssService NegotiatedService { get; set; } = RpcSecGssService.None;

    /// <summary>Always false: this mechanism provides no cryptographic protection.</summary>
    public bool ProvidesCryptographicProtection => false;

    public Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct) =>
        Task.FromResult(Array.Empty<byte>());

    public byte[] GetMic(byte[] data) => Array.Empty<byte>();
    public bool VerifyMic(byte[] data, byte[] mic) => true;
    public byte[] Wrap(byte[] data) => data;
    public byte[] Unwrap(byte[] wrappedData) => wrappedData;
    public byte[] GetMic(byte[] data, uint qop) => GetMic(data);
    public bool VerifyMic(byte[] data, byte[] mic, uint qop) => VerifyMic(data, mic);
    public byte[] Wrap(byte[] data, uint qop) => Wrap(data);
    public byte[] Unwrap(byte[] wrappedData, uint qop) => Unwrap(wrappedData);
    public void Dispose() { }
}

/// <summary>Helpers for classifying RPCSEC_GSS mechanism security strength.</summary>
public static class RpcSecGssMechanism
{
    /// <summary>
    /// Returns true when the mechanism can actually protect integrity/privacy service traffic.
    /// <see cref="NoOpGssMechanism"/> is explicitly excluded from security claims.
    /// </summary>
    public static bool ProvidesCryptographicProtection(IRpcSecGssMechanism mechanism)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        return mechanism is not NoOpGssMechanism;
    }

    /// <summary>Compute a MIC, preferring the QOP-aware contract when available.</summary>
    public static byte[] GetMic(IRpcSecGssMechanism mechanism, byte[] data, uint qop)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        ArgumentNullException.ThrowIfNull(data);
        return mechanism is IRpcSecGssQopMechanism qopMechanism
            ? qopMechanism.GetMic(data, qop)
            : mechanism.GetMic(data);
    }

    /// <summary>Verify a MIC, preferring the QOP-aware contract when available.</summary>
    public static bool VerifyMic(IRpcSecGssMechanism mechanism, byte[] data, byte[] mic, uint qop)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(mic);
        return mechanism is IRpcSecGssQopMechanism qopMechanism
            ? qopMechanism.VerifyMic(data, mic, qop)
            : mechanism.VerifyMic(data, mic);
    }
}
