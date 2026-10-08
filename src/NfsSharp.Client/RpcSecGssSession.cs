using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Holds RPCSEC_GSS context state and writes CALL credentials/verifiers (RFC 2203).</summary>
internal sealed class RpcSecGssSession
{
    private readonly NfsClientOptions _options;
    private readonly ILogger? _logger;
    private RpcSecGssContext? _context;

    internal RpcSecGssSession(NfsClientOptions options)
    {
        _options = options;
        _logger = options.Logger;
    }

    internal bool IsEstablished => _context?.Mechanism.IsEstablished == true;

    /// <summary>Write the RPCSEC_GSS credential body: context handle, sequence number, service.</summary>
    internal void WriteCredential(XdrWriter writer)
    {
        var context = _context ?? throw new InvalidOperationException("RPCSEC_GSS context is unavailable.");
        var body = new XdrWriter();
        body.UInt((uint)context.ContextHandle.Length);
        body.Opaque(context.ContextHandle);
        body.UInt(context.Mechanism.NextSeqNum++);
        body.UInt((uint)context.Service);
        writer.Opaque(body.ToArray());
    }

    /// <summary>Write the RPCSEC_GSS verifier: a GSS MIC over the procedure arguments.</summary>
    internal void WriteVerifier(XdrWriter writer, ReadOnlySpan<byte> arguments)
    {
        var context = _context ?? throw new InvalidOperationException("RPCSEC_GSS context is unavailable.");
        writer.Opaque(context.Mechanism.GetMic(arguments.ToArray()));
    }

    /// <summary>Observe a REPLY verifier so security-session state can track it.</summary>
    internal void ObserveReply(RpcReply reply)
    {
        if (_context is not null && reply.VerifierFlavor == (uint)RpcSecGssFlavor.Gss)
        {
            // M4 owns full response verifier validation. Keeping this hook here prevents
            // the generic RPC layer from owning security-session state.
            _ = reply.Verifier;
        }
    }

    /// <summary>Run RPCSEC_GSS_CREATE and store the resulting server context handle.</summary>
    internal async Task EstablishAsync(string server, RpcClient rpcClient, CancellationToken ct)
    {
        var mechanism = _options.GssMechanism
                        ?? throw new InvalidOperationException("A GSS mechanism was not configured.");
        var targetName = _options.GssTargetName ?? $"nfs/{server}";
        var token = await mechanism.InitiateContextAsync(targetName, _options.GssCredentials, ct);

        // CREATE arguments: procedure, GSS token, requested service, sequence-window hint.
        var arguments = new XdrWriter();
        arguments.UInt((uint)RpcSecGssProc.Create);
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
        var status = reader.UInt();
        if (status != 0)
            throw new NfsException($"RPCSEC_GSS_CREATE failed (stat={status}).");

        _context = new RpcSecGssContext
        {
            ContextHandle = reader.Opaque(),
            SeqWindowSize = reader.UInt(),
            SeqWindow = reader.FixedBytes(8),
            Service = _options.GssService,
            Mechanism = mechanism,
        };
        mechanism.NegotiatedService = _options.GssService;
        _logger?.LogInformation(
            "RPCSEC_GSS context established (target={Target}, service={Service})",
            targetName,
            _options.GssService);
    }
}
