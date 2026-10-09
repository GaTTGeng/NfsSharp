using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Holds RPCSEC_GSS context state and writes CALL credentials/verifiers (RFC 2203).</summary>
internal sealed class RpcSecGssSession
{
    private readonly NfsClientOptions _options;
    private readonly ILogger? _logger;
    private RpcSecGssContext? _context;
    private uint _contextGeneration;
    private uint _nextSeqNum = 1;

    internal RpcSecGssSession(NfsClientOptions options)
    {
        _options = options;
        _logger = options.Logger;
    }

    internal bool IsEstablished => _context?.Mechanism.IsEstablished == true;

    /// <summary>Generation counter for the active GSS context; bumped whenever a context is published.</summary>
    internal uint ContextGeneration => _contextGeneration;

    /// <summary>Install a fully established context. Used by establishment and deterministic tests.</summary>
    internal void InstallContext(RpcSecGssContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ContextHandle is null)
            throw new ArgumentException("Context handle is required.", nameof(context));
        // RFC 2203 does not cap seq_window at 64; only a zero window is protocol-invalid.
        if (context.SeqWindowSize == 0)
            throw new ArgumentException("Sequence window must be greater than zero.", nameof(context));

        _context = context;
        _contextGeneration++;
        _nextSeqNum = 1;
        context.Mechanism.NegotiatedService = context.Service;
    }

    /// <summary>
    /// Allocate the immutable security record for one transmitted attempt.
    /// Each attempt gets a fresh sequence number even when an RPC XID is reused on retry.
    /// </summary>
    internal RpcSecGssCallRecord BeginDataCall(uint xid, uint program, uint version, uint procedure)
    {
        var context = _context ?? throw new InvalidOperationException("RPCSEC_GSS context is unavailable.");
        var seqNum = _nextSeqNum;
        if (seqNum == 0 || seqNum >= RpcSecGssConstants.MaxSeq)
            throw new NfsException($"RPCSEC_GSS sequence space exhausted (seq={seqNum}).");
        _nextSeqNum = seqNum + 1;

        return new RpcSecGssCallRecord(
            xid,
            seqNum,
            context.Service,
            RpcSecGssConstants.DefaultQop,
            _contextGeneration,
            program,
            version,
            procedure,
            RpcSecGssProc.Data,
            context.ContextHandle);
    }

    /// <summary>Write the RFC 2203 rpc_gss_cred_t credential body for <paramref name="record"/>.</summary>
    internal void WriteCredential(XdrWriter writer, RpcSecGssCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(record);

        // rpc_gss_cred_t is an XDR union switch on version; version 1 body follows.
        var body = new XdrWriter();
        body.UInt(RpcSecGssConstants.Version);
        body.UInt((uint)record.GssProc);
        body.UInt(record.SeqNum);
        body.UInt((uint)record.Service);
        body.Opaque(record.ContextHandle);
        writer.Opaque(body.ToArray());
    }

    /// <summary>
    /// Compute the RPCSEC_GSS request verifier: a GSS MIC over the RPC header up to and
    /// including the credential (RFC 2203 §5.3.1), using the attempt QOP.
    /// Returns the verifier body to place in the opaque_auth after its RPCSEC_GSS flavor.
    /// </summary>
    internal byte[] CreateHeaderVerifier(ReadOnlySpan<byte> headerThroughCredential, RpcSecGssCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var context = _context ?? throw new InvalidOperationException("RPCSEC_GSS context is unavailable.");
        if (!ReferenceEquals(context.Mechanism, _options.GssMechanism) || context.Service != record.Service)
            throw new NfsException("RPCSEC_GSS context changed while building a call.");

        var mic = RpcSecGssMechanism.GetMic(context.Mechanism, headerThroughCredential.ToArray(), record.Qop);
        if (mic.Length == 0)
            throw new NfsException("RPCSEC_GSS mechanism produced an empty header checksum.");
        return mic;
    }

    /// <summary>
    /// Verify an accepted reply's RPCSEC_GSS verifier before any procedure result is exposed.
    /// The verifier must be the MIC of the request sequence number in network byte order (RFC 2203 §5.3.3.2).
    /// </summary>
    internal void VerifyReply(RpcReply reply, RpcSecGssCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(record);

        var context = _context ?? throw new NfsException("RPCSEC_GSS context is unavailable.");
        if (record.ContextGeneration != _contextGeneration || !ReferenceEquals(context.Mechanism, _options.GssMechanism))
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: context generation mismatch ({record}).");

        if (reply.VerifierFlavor != (uint)RpcSecGssFlavor.Gss)
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: expected flavor RPCSEC_GSS, got {reply.VerifierFlavor} ({record}).");

        if (reply.Verifier.Length == 0)
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: verifier body is empty ({record}).");

        var seqBytes = record.SeqNumNetworkOrder();
        bool verified;
        try
        {
            verified = RpcSecGssMechanism.VerifyMic(context.Mechanism, seqBytes, reply.Verifier, record.Qop);
        }
        catch (Exception ex)
        {
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: mechanism failed to verify the sequence number MIC ({record}).",
                ex);
        }

        if (!verified)
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: sequence number MIC mismatch ({record}).");

        _logger?.LogDebug(
            "RPCSEC_GSS reply verifier accepted ({Record})",
            record.ToString());
    }

    /// <summary>Run RPCSEC_GSS context establishment and store the resulting server context handle.</summary>
    internal async Task EstablishAsync(string server, RpcClient rpcClient, CancellationToken ct)
    {
        var mechanism = _options.GssMechanism
                        ?? throw new InvalidOperationException("A GSS mechanism was not configured.");
        // Header MICs are mandatory for every data call, including rpc_gss_svc_none.
        if (!RpcSecGssMechanism.CanComputeMic(mechanism))
        {
            throw new NfsException(
                "The configured GSS mechanism cannot compute the RPCSEC_GSS data-call header verifier " +
                "(NegotiateGssMechanism does not expose GSS_GetMIC) and cannot be used for any service, including none.");
        }

        if (_options.GssService != RpcSecGssService.None &&
            !RpcSecGssMechanism.ProvidesCryptographicProtection(mechanism))
        {
            throw new NfsException(
                "The configured GSS mechanism does not provide cryptographic integrity/privacy " +
                "(NoOpGssMechanism and NegotiateGssMechanism are excluded) and cannot be used with integrity or privacy service.");
        }

        // Default target principal follows the conventional nfs/<host> service name.
        var targetName = _options.GssTargetName ?? $"nfs/{server}";
        // Phase 1: local GSS context initiation produces the token the server must consume.
        var token = await mechanism.InitiateContextAsync(targetName, _options.GssCredentials, ct);

        // Context-establishment call data is still the pre-RFC layout used by the
        // experimental CREATE path; #85 rewrites this to rpc_gss_init_arg over NULLPROC
        // with the control procedure carried in rpc_gss_cred_t. Keep wire value 0 here
        // so this slice does not silently change establishment bytes.
        var arguments = new XdrWriter();
        arguments.UInt((uint)RpcSecGssProc.Data);
        arguments.UInt((uint)token.Length);
        arguments.Opaque(token);
        arguments.UInt((uint)_options.GssService);
        arguments.UInt(0);

        // CREATE is not idempotent; a retry would allocate a second server context.
        var reader = await rpcClient.CallRawAsync(
            NfsRpcConstants.ProgNfs,
            NfsRpcConstants.VerNfs,
            0,
            arguments.ToArray(),
            ct);
        // Phase 2: decode the CREATE result before publishing any session state.
        var status = reader.UInt();
        if (status != 0)
            throw new NfsException($"RPCSEC_GSS_CREATE failed (stat={status}).");

        // Publish the context only after the full reply decodes, so later CALLs never sign with a partial session.
        var contextHandle = reader.Opaque();
        var seqWindow = reader.UInt();
        // RFC 2203 lets the server select any positive window; 64 is only a local default.
        if (seqWindow == 0)
            throw new NfsException("RPCSEC_GSS_CREATE returned an invalid sequence window: 0.");

        InstallContext(new RpcSecGssContext
        {
            ContextHandle = contextHandle,
            SeqWindowSize = seqWindow,
            SeqWindow = reader.FixedBytes(8),
            Service = _options.GssService,
            Mechanism = mechanism,
        });
        _logger?.LogInformation(
            "RPCSEC_GSS context established (target={Target}, service={Service}, contextGeneration={ContextGeneration})",
            targetName,
            _options.GssService,
            _contextGeneration);
    }
}
