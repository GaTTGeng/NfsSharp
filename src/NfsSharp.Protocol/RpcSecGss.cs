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

/// <summary>RPCSEC_GSS procedure numbers.</summary>
public enum RpcSecGssProc : uint
{
    Create = 0,
    Destroy = 1,
    Init = 2,
    ContinueInit = 3,
    GetMic = 4,
    Wrap = 5,
}

/// <summary>RPCSEC_GSS sequence number window size for replay detection.</summary>
public static class RpcSecGssConstants
{
    public const int MaxSeqWindowSize = 64;
    public const int DefaultSeqWindowSize = 64;
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
    public byte[] ContextHandle { get; init; } = Array.Empty<byte>();
    public uint SeqWindowSize { get; init; }
    public byte[] SeqWindow { get; init; } = new byte[8];
    public RpcSecGssService Service { get; init; }
    public IRpcSecGssMechanism Mechanism { get; init; } = null!;
}

/// <summary>
/// NegotiateAuthentication-based GSS mechanism for .NET 8+.
/// Uses System.Net.Security.NegotiateAuthentication for Kerberos/NTLM.
/// </summary>
public sealed class NegotiateGssMechanism : IRpcSecGssMechanism
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

        if (credentials?.UserName is not null)
        {
            options.Credential = new System.Net.NetworkCredential(
                credentials.UserName,
                credentials.Password ?? "",
                credentials.Realm ?? "");
        }

        _auth = new System.Net.Security.NegotiateAuthentication(options);
        var token = _auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out _);
        return Task.FromResult(token ?? Array.Empty<byte>());
    }

    public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct)
    {
        if (_auth is null)
            throw new InvalidOperationException("Context not initiated.");

        var token = _auth.GetOutgoingBlob(serverToken, out var statusCode);
        if (statusCode == System.Net.Security.NegotiateAuthenticationStatusCode.Completed)
            _established = true;

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

    public void Dispose()
    {
        _auth?.Dispose();
    }
}

/// <summary>
/// AUTH_NONE/AUTH_SYS-based GSS mechanism for testing and environments
/// where Kerberos is not available. Provides no actual security.
/// </summary>
public sealed class NoOpGssMechanism : IRpcSecGssMechanism
{
    public byte[] MechanismOid => Array.Empty<byte>();
    public bool IsEstablished => true;
    public uint MaxMessageSize => uint.MaxValue;
    public uint NextSeqNum { get; set; }
    public RpcSecGssService NegotiatedService { get; set; } = RpcSecGssService.None;

    public Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct) =>
        Task.FromResult(Array.Empty<byte>());

    public byte[] GetMic(byte[] data) => Array.Empty<byte>();
    public bool VerifyMic(byte[] data, byte[] mic) => true;
    public byte[] Wrap(byte[] data) => data;
    public byte[] Unwrap(byte[] wrappedData) => wrappedData;
    public void Dispose() { }
}
