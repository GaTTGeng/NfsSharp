using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Holds RPCSEC_GSS context state and writes CALL credentials/verifiers (RFC 2203).</summary>
internal sealed class RpcSecGssSession
{
    private readonly NfsClientOptions _options;
    private readonly ILogger? _logger;
    // System.Threading.Lock is unavailable on net8.0.
    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    private readonly object _stateLock = new();
    private RpcSecGssContext? _context;
    private SemaphoreSlim _callSlots;
    private uint? _rejectedAuthStatus;
    private uint _contextGeneration;
    private uint _nextSeqNum = 1;

    internal RpcSecGssSession(NfsClientOptions options)
    {
        _options = options;
        _logger = options.Logger;
        _callSlots = new SemaphoreSlim(options.MaxOutstandingRpcCallsPerConnection);
    }

    internal bool IsEstablished
    {
        get
        {
            lock (_stateLock)
                return _rejectedAuthStatus is null && _context?.Mechanism.IsEstablished == true;
        }
    }

    /// <summary>Whether RPC data calls must use GSS or fail closed instead of falling back to AUTH_SYS.</summary>
    internal bool RequiresGss
    {
        get
        {
            lock (_stateLock)
                return _context is not null || _rejectedAuthStatus is not null;
        }
    }

    /// <summary>Generation counter for the active GSS context; bumped whenever a context is published.</summary>
    // Used by session and record validation; keep the accessor for the state contract.
    // ReSharper disable once UnusedMember.Global
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

        lock (_stateLock)
        {
            _context = context;
            _rejectedAuthStatus = null;
            _contextGeneration++;
            _nextSeqNum = 1;
            var maxOutstanding = (int)Math.Min(
                context.SeqWindowSize,
                (uint)_options.MaxOutstandingRpcCallsPerConnection);
            _callSlots = new SemaphoreSlim(maxOutstanding);
            context.Mechanism.NegotiatedService = context.Service;
        }
    }

    /// <summary>Wait for an authenticated request slot bounded by both the peer window and local RPC limit.</summary>
    internal async Task AcquireCallSlotAsync(CancellationToken ct)
    {
        SemaphoreSlim slots;
        lock (_stateLock)
        {
            EnsureContextAvailable();
            slots = _callSlots;
        }

        await slots.WaitAsync(ct);
        lock (_stateLock)
        {
            if (!ReferenceEquals(slots, _callSlots) || _rejectedAuthStatus is not null)
            {
                slots.Release();
                EnsureContextAvailable();
                throw new NfsException("RPCSEC_GSS context changed while waiting for a request slot.");
            }
        }
    }

    /// <summary>Release one authenticated request slot after its reply or failure is handled.</summary>
    internal void ReleaseCallSlot() => _callSlots.Release();

    /// <summary>Mark the session unusable after the server rejects its credential or context.</summary>
    internal void RejectContext(uint authStatus)
    {
        if (authStatus is not (13 or 14))
            throw new ArgumentOutOfRangeException(nameof(authStatus));

        lock (_stateLock)
        {
            _rejectedAuthStatus = authStatus;
            _contextGeneration++;
        }
    }

    /// <summary>
    /// Allocate the immutable security record for one transmitted attempt.
    /// Each attempt gets a fresh sequence number even when an RPC XID is reused on retry.
    /// </summary>
    internal RpcSecGssCallRecord BeginDataCall(uint xid, uint program, uint version, uint procedure)
    {
        lock (_stateLock)
        {
            EnsureContextAvailable();
            var context = _context!;
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
        catch (Exception)
        {
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: mechanism failed to verify the sequence number MIC ({record}).");
        }

        if (!verified)
            throw new NfsException(
                $"RPCSEC_GSS reply verifier rejected: sequence number MIC mismatch ({record}).");

        _logger?.LogDebug(
            "RPCSEC_GSS reply verifier accepted ({Record})",
            record.ToString());
    }

    /// <summary>Protect procedure arguments according to the attempt's RFC 2203 service.</summary>
    internal byte[] ProtectArguments(byte[] arguments, RpcSecGssCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(record);

        if (record.Service == RpcSecGssService.None)
            return arguments;

        var context = RequireCallContext(record);
        var databody = new XdrWriter();
        databody.UInt(record.SeqNum);
        databody.Raw(arguments);
        var databodyBytes = databody.ToArray();
        var protectedBody = new XdrWriter();

        switch (record.Service)
        {
            case RpcSecGssService.Integrity:
                // RFC 2203 §5.3.2.2 MIC covers the databody bytes, excluding both opaque wrappers.
                byte[] checksum;
                try
                {
                    checksum = RpcSecGssMechanism.GetMic(context.Mechanism, databodyBytes, record.Qop);
                }
                catch
                {
                    throw new NfsException($"RPCSEC_GSS mechanism failed to protect integrity arguments ({record}).");
                }
                if (checksum.Length == 0)
                    throw new NfsException($"RPCSEC_GSS mechanism produced an empty argument MIC ({record}).");
                protectedBody.Opaque(databodyBytes);
                protectedBody.Opaque(checksum);
                break;

            case RpcSecGssService.Privacy:
                // External mechanisms may implement both the base and privacy extension interfaces.
                // ReSharper disable once SuspiciousTypeConversion.Global
                var privacyMechanism = context.Mechanism as IRpcSecGssPrivacyMechanism
                    ?? throw new NfsException(
                        $"RPCSEC_GSS privacy requires a mechanism that reports confidentiality and QOP ({record}).");
                RpcSecGssPrivacyResult wrapped;
                try
                {
                    wrapped = privacyMechanism.WrapPrivacy(databodyBytes, record.Qop);
                }
                catch
                {
                    throw new NfsException($"RPCSEC_GSS mechanism failed to protect privacy arguments ({record}).");
                }
                if (wrapped.Data is null || wrapped.Data.Length == 0)
                    throw new NfsException($"RPCSEC_GSS mechanism produced an empty privacy body ({record}).");
                if (wrapped.Qop != record.Qop || !wrapped.ConfidentialityEstablished)
                    throw new NfsException($"RPCSEC_GSS mechanism did not provide the requested privacy protection ({record}).");
                protectedBody.Opaque(wrapped.Data);
                break;

            default:
                throw new NfsException($"Unsupported RPCSEC_GSS service {record.Service} ({record}).");
        }

        return protectedBody.ToArray();
    }

    /// <summary>Authenticate and unwrap a successful procedure result before exposing it to an NFS decoder.</summary>
    internal XdrReader UnprotectReplyBody(RpcReply reply, RpcSecGssCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(record);

        if (record.Service == RpcSecGssService.None)
            return reply.Body;

        var context = RequireCallContext(record);
        try
        {
            byte[] databodyBytes;
            var envelope = new XdrReader(reply.Body.ReadRemainingBytes());
            if (record.Service == RpcSecGssService.Integrity)
            {
                // The first opaque contains seq_num + results; the second carries its MIC.
                databodyBytes = envelope.Opaque();
                var checksum = envelope.Opaque();
                if (envelope.Remaining != 0)
                    throw new NfsException("RPCSEC_GSS integrity result contains trailing data.");
                bool verified;
                try
                {
                    verified = checksum.Length != 0 && RpcSecGssMechanism.VerifyMic(
                        context.Mechanism,
                        databodyBytes,
                        checksum,
                        record.Qop);
                }
                catch
                {
                    throw new NfsException("RPCSEC_GSS mechanism failed to verify the integrity result MIC.");
                }
                if (!verified)
                    throw new NfsException("RPCSEC_GSS integrity result MIC verification failed.");
            }
            else if (record.Service == RpcSecGssService.Privacy)
            {
                var wrapped = envelope.Opaque();
                if (envelope.Remaining != 0)
                    throw new NfsException("RPCSEC_GSS privacy result contains trailing data.");
                // External mechanisms may implement both the base and privacy extension interfaces.
                // ReSharper disable once SuspiciousTypeConversion.Global
                var privacyMechanism = context.Mechanism as IRpcSecGssPrivacyMechanism
                    ?? throw new NfsException("RPCSEC_GSS privacy requires a mechanism that reports confidentiality and QOP.");
                RpcSecGssPrivacyResult unwrapped;
                try
                {
                    unwrapped = privacyMechanism.UnwrapPrivacy(wrapped, record.Qop);
                }
                catch
                {
                    throw new NfsException("RPCSEC_GSS mechanism failed to unwrap the privacy result.");
                }
                if (unwrapped.Data is null || unwrapped.Qop != record.Qop || !unwrapped.ConfidentialityEstablished)
                    throw new NfsException("RPCSEC_GSS privacy result did not provide the requested confidentiality and QOP.");
                databodyBytes = unwrapped.Data;
            }
            else
            {
                throw new NfsException($"Unsupported RPCSEC_GSS service {record.Service}.");
            }

            var data = new XdrReader(databodyBytes);
            var returnedSequence = data.UInt();
            if (returnedSequence != record.SeqNum)
                throw new NfsException(
                    $"RPCSEC_GSS protected result sequence mismatch: expected {record.SeqNum}, got {returnedSequence} ({record}).");

            return new XdrReader(data.ReadRemainingBytes());
        }
        catch (NfsException ex)
        {
            if (ex.Message.Contains(record.ToString(), StringComparison.Ordinal))
                throw;
            throw new NfsException($"RPCSEC_GSS protected result rejected ({record}): {ex.Message}");
        }
        catch (Exception ex)
        {
            throw new NfsException($"RPCSEC_GSS protected result rejected due to {ex.GetType().Name} ({record}).");
        }
    }

    private RpcSecGssContext RequireCallContext(RpcSecGssCallRecord record)
    {
        lock (_stateLock)
        {
            EnsureContextAvailable();
            var context = _context!;
            if (record.ContextGeneration != _contextGeneration ||
                !ReferenceEquals(context.Mechanism, _options.GssMechanism) ||
                context.Service != record.Service)
                throw new NfsException($"RPCSEC_GSS context changed while processing a protected body ({record}).");
            return context;
        }
    }

    private void EnsureContextAvailable()
    {
        if (_rejectedAuthStatus is { } authStatus)
            throw new NfsException(
                $"RPCSEC_GSS context was rejected by the server (auth_stat={authStatus}); establish a new NFS client.",
                inner: null,
                rpcAuthStatus: authStatus);
        if (_context is null || !_context.Mechanism.IsEstablished)
            throw new NfsException("RPCSEC_GSS context is unavailable; establish a new NFS client.");
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
        try
        {
            var token = await InitiateMechanismAsync(mechanism, targetName, _options.GssCredentials, ct);
            var controlProcedure = RpcSecGssProc.Init;
            byte[] contextHandle = [];
            var clientNeedsFinalToken = !mechanism.IsEstablished;
            var contextCompleted = false;

            // Each continuation is a non-idempotent context-creation call, so it is sent once.
            for (var round = 0; round < MaxContextCreationRounds; round++)
            {
                var arguments = new XdrWriter();
                arguments.Opaque(token);
                var reply = await rpcClient.CallRpcSecGssContextAsync(
                    controlProcedure,
                    contextHandle,
                    arguments.ToArray(),
                    ct);

                // The RPC envelope verifier is part of context establishment and must survive
                // decoding so the final seq_window can be authenticated before publishing state.
                var result = reply.Body;
                var returnedHandle = result.Opaque();
                var majorStatus = result.UInt();
                var minorStatus = result.UInt();
                var returnedSeqWindow = result.UInt();
                var serverToken = result.Opaque();
                if (result.Remaining != 0)
                    throw new NfsException("RPCSEC_GSS context response contains trailing data.");

                if (majorStatus is not (GssComplete or GssContinueNeeded))
                {
                    RequireNullContextVerifier(reply);
                    if (returnedHandle.Length != 0 || serverToken.Length != 0)
                        throw new NfsException("RPCSEC_GSS failed context response returned a handle or token.");
                    throw new NfsException(
                        $"RPCSEC_GSS context establishment failed (gss_major={majorStatus}, gss_minor={minorStatus}).");
                }

                if (returnedHandle.Length == 0)
                    throw new NfsException("RPCSEC_GSS context response returned an empty context handle.");
                if (contextHandle.Length != 0 && !contextHandle.AsSpan().SequenceEqual(returnedHandle))
                    throw new NfsException("RPCSEC_GSS context handle changed during continuation.");
                if (returnedSeqWindow == 0)
                    throw new NfsException("RPCSEC_GSS context response returned an invalid sequence window: 0.");

                contextHandle = returnedHandle;

                if (majorStatus == GssContinueNeeded)
                {
                    RequireNullContextVerifier(reply);
                    token = await ContinueMechanismAsync(mechanism, serverToken, ct);
                    if (token.Length == 0)
                        throw new NfsException("RPCSEC_GSS mechanism returned no token while the server requested continuation.");
                    clientNeedsFinalToken = !mechanism.IsEstablished;
                    controlProcedure = RpcSecGssProc.ContinueInit;
                    continue;
                }

                if (clientNeedsFinalToken)
                {
                    await ContinueMechanismAsync(mechanism, serverToken, ct);
                }
                if (!mechanism.IsEstablished)
                    throw new NfsException("RPCSEC_GSS server completed context establishment before the client mechanism.");

                // The mechanism must consume its final server token before it can verify this MIC.
                // GSS_S_COMPLETE authenticates the four-byte network-order seq_window with QOP 0.
                VerifyContextCompletionVerifier(mechanism, reply, returnedSeqWindow);

                InstallContext(new RpcSecGssContext
                {
                    ContextHandle = contextHandle,
                    SeqWindowSize = returnedSeqWindow,
                    SeqWindow = new byte[8],
                    Service = _options.GssService,
                    Mechanism = mechanism,
                });
                contextCompleted = true;
                break;
            }

            if (!contextCompleted)
                throw new NfsException($"RPCSEC_GSS context establishment exceeded {MaxContextCreationRounds} rounds.");
        }
        catch
        {
            // A failed or cancelled handshake must not leave a partial mechanism usable.
            _context = null;
            mechanism.Dispose();
            throw;
        }

        _logger?.LogInformation(
            "RPCSEC_GSS context established (target={Target}, service={Service}, contextGeneration={ContextGeneration})",
            targetName,
            _options.GssService,
            _contextGeneration);
    }

    private const uint GssComplete = 0;
    private const uint GssContinueNeeded = 1;
    private const int MaxContextCreationRounds = 16;

    private static async Task<byte[]> InitiateMechanismAsync(
        IRpcSecGssMechanism mechanism,
        string targetName,
        GssCredentials? credentials,
        CancellationToken ct)
    {
        try
        {
            return await mechanism.InitiateContextAsync(targetName, credentials, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new NfsException("RPCSEC_GSS mechanism failed during context initiation; mechanism details were redacted.");
        }
    }

    private static async Task<byte[]> ContinueMechanismAsync(
        IRpcSecGssMechanism mechanism,
        byte[] serverToken,
        CancellationToken ct)
    {
        try
        {
            return await mechanism.ContinueContextAsync(serverToken, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new NfsException("RPCSEC_GSS mechanism failed during context continuation; mechanism details were redacted.");
        }
    }

    private static void RequireNullContextVerifier(RpcReply reply)
    {
        if (reply.VerifierFlavor != (uint)RpcSecGssFlavor.None || reply.Verifier.Length != 0)
            throw new NfsException(
                "RPCSEC_GSS context response verifier must be an empty AUTH_NONE verifier until GSS_S_COMPLETE.");
    }

    private static void VerifyContextCompletionVerifier(
        IRpcSecGssMechanism mechanism,
        RpcReply reply,
        uint seqWindow)
    {
        if (reply.VerifierFlavor != (uint)RpcSecGssFlavor.Gss)
            throw new NfsException(
                $"RPCSEC_GSS completed context verifier rejected: expected flavor RPCSEC_GSS, got {reply.VerifierFlavor}.");
        if (reply.Verifier.Length == 0)
            throw new NfsException("RPCSEC_GSS completed context verifier body is empty.");

        Span<byte> seqWindowBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(seqWindowBytes, seqWindow);
        bool verified;
        try
        {
            verified = RpcSecGssMechanism.VerifyMic(
                mechanism,
                seqWindowBytes.ToArray(),
                reply.Verifier,
                RpcSecGssConstants.DefaultQop);
        }
        catch (Exception)
        {
            throw new NfsException("RPCSEC_GSS completed context verifier mechanism failed to verify seq_window; mechanism details were redacted.");
        }

        if (!verified)
            throw new NfsException("RPCSEC_GSS completed context verifier MIC mismatch for seq_window.");
    }
}
