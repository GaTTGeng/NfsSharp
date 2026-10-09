using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Issues ONC RPC CALLs and returns the decoded procedure result.</summary>
internal interface IRpcCallClient
{
    /// <summary>Send one RPC CALL and return the procedure result payload.</summary>
    Task<XdrReader> CallAsync(
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct);
}

/// <summary>ONC RPC CALL/REPLY client multiplexing outstanding calls over one TCP connection.</summary>
internal sealed class RpcClient : IRpcCallClient, IAsyncDisposable
{
    private readonly RpcTransport _transport;
    private readonly NfsClientOptions _options;
    private readonly NfsRetryPolicy _retryPolicy;
    private readonly RpcSecGssSession _gssSession;
    private readonly byte[] _authSysBody;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gssCallLock = new(1, 1);
    private readonly SemaphoreSlim _connectionStateLock = new(1, 1);
    private readonly CancellationTokenSource _stopSource = new();

    private RpcConnection? _activeConnection;
    private int _activePort;
    private int _xid;
    private int _stopping;

    internal RpcClient(
        RpcTransport transport,
        NfsClientOptions options,
        NfsRetryPolicy retryPolicy,
        RpcSecGssSession gssSession,
        byte[] authSysBody)
    {
        _transport = transport;
        _options = options;
        _retryPolicy = retryPolicy;
        _gssSession = gssSession;
        _authSysBody = authSysBody;
        _logger = options.Logger;
    }

    /// <summary>Wrap an already-connected stream instead of opening a socket.</summary>
    internal RpcClient(Stream stream, NfsClientOptions options)
        : this(
            new RpcTransport(IPAddress.Loopback, options),
            options,
            new NfsRetryPolicy(options),
            new RpcSecGssSession(options),
            RpcAuthSysCredentials.Encode(options))
    {
        _activeConnection = new RpcConnection(
            stream,
            maxOutstandingCalls: options.MaxOutstandingRpcCallsPerConnection,
            logger: options.Logger,
            recordCompletionTimeout: options.CommandTimeout);
    }

    /// <summary>Connect to <paramref name="port"/> and install the connection as the active one.</summary>
    internal async Task ConnectAsync(int port, CancellationToken ct)
    {
        var connection = await _transport.OpenAsync(port, ct);
        // Publish the new generation atomically; dispose whatever it replaced afterwards.
        var previous = Interlocked.Exchange(ref _activeConnection, connection);
        _activePort = port;
        if (previous is not null)
            await previous.DisposeAsync();
    }

    /// <summary>Send one RPC CALL on the active connection and return the procedure result payload.</summary>
    public async Task<XdrReader> CallAsync(
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct)
    {
        // Resolve a healthy connection up front so the first attempt never starts on a dead generation.
        var connection = await RequireHealthyConnectionAsync(ct);
        for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                return await CallOnceAsync(connection, program, version, procedure, arguments, ct);
            }
            catch (Exception ex) when (NfsRetryPolicy.IsTransient(ex) &&
                                       attempt < _retryPolicy.MaxAttempts &&
                                       NfsRetryPolicy.CanRetry(program, version, procedure) &&
                                       program == NfsRpcConstants.ProgNfs &&
                                       version == NfsRpcConstants.VerNfs)
            {
                // A failed CALL may leave the shared stream unusable; retry only
                // retry-safe NFSv3 procedures, and do so on a fresh connection.
                _logger?.LogWarning(
                    ex,
                    "RPC call failed transiently (attempt {Attempt}/{MaxAttempts}, prog={Program}, proc={Procedure})",
                    attempt,
                    _retryPolicy.MaxAttempts,
                    program,
                    procedure);
                // First retry-safe attempt after reconnect; non-idempotent procedures never replay.
                connection = await ReconnectAsync(connection, ct) ?? connection;
                await _retryPolicy.DelayAsync(ct);
            }
            catch (Exception ex) when (NfsRetryPolicy.IsTransient(ex) && attempt < _retryPolicy.MaxAttempts)
            {
                // Transient but not retry-safe: surface the failure rather than replaying a mutation.
                _logger?.LogWarning(
                    ex,
                    "RPC call failed transiently without automatic retry because the procedure is not retry-safe (prog={Program}, proc={Procedure})",
                    program,
                    procedure);
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested &&
                                                         !_stopSource.IsCancellationRequested &&
                                                         _options.CommandTimeout > TimeSpan.Zero)
            {
                // Command timeout (not caller/stop cancellation) is reported as a domain error.
                _logger?.LogError(
                    ex,
                    "RPC call timed out after {Timeout} (prog={Program}, proc={Procedure})",
                    _options.CommandTimeout,
                    program,
                    procedure);
                throw new NfsException($"RPC call timed out after {_options.CommandTimeout}.", ex);
            }
        }

        throw new NfsException("RPC call failed after all retry attempts.");
    }

    /// <summary>Run one RPC on a dedicated short-lived connection (portmap/MOUNT setup calls).</summary>
    internal async Task<XdrReader> CallWithOwnedConnectionAsync(
        int port,
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            // Each attempt owns a fresh short-lived connection; setup calls must not share the NFS stream.
            RpcConnection? connection = null;
            try
            {
                connection = await _transport.OpenAsync(port, ct);
                return await CallOnceAsync(connection, program, version, procedure, arguments, ct);
            }
            catch (Exception ex) when (NfsRetryPolicy.IsTransient(ex) &&
                                       attempt < _retryPolicy.MaxAttempts &&
                                       NfsRetryPolicy.CanRetry(program, version, procedure))
            {
                _logger?.LogWarning(
                    ex,
                    "RPC call failed transiently (attempt {Attempt}/{MaxAttempts}, prog={Program}, proc={Procedure})",
                    attempt,
                    _retryPolicy.MaxAttempts,
                    program,
                    procedure);
                await _retryPolicy.DelayAsync(ct);
            }
            catch (Exception ex) when (NfsRetryPolicy.IsTransient(ex) && attempt < _retryPolicy.MaxAttempts)
            {
                _logger?.LogWarning(
                    ex,
                    "RPC call failed transiently without automatic retry because the procedure is not retry-safe (prog={Program}, proc={Procedure})",
                    program,
                    procedure);
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested &&
                                                         !_stopSource.IsCancellationRequested &&
                                                         _options.CommandTimeout > TimeSpan.Zero)
            {
                _logger?.LogError(
                    ex,
                    "RPC call timed out after {Timeout} (prog={Program}, proc={Procedure})",
                    _options.CommandTimeout,
                    program,
                    procedure);
                throw new NfsException($"RPC call timed out after {_options.CommandTimeout}.", ex);
            }
            finally
            {
                // Always release the per-attempt connection, success or failure.
                if (connection is not null)
                    await connection.DisposeAsync();
            }
        }

        throw new NfsException("RPC call failed after all retry attempts.");
    }

    /// <summary>Send one RPC CALL without retries (non-idempotent setup such as RPCSEC_GSS CREATE).</summary>
    internal async Task<XdrReader> CallRawAsync(
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct) =>
        await CallOnceAsync(
            await RequireHealthyConnectionAsync(ct),
            program,
            version,
            procedure,
            arguments,
            ct);

    internal async ValueTask DisposeActiveConnectionForTestingAsync() =>
        await RequireActiveConnection().DisposeAsync();

    internal int PendingCallCountForTesting => _activeConnection?.PendingCallCount ?? 0;
    internal int PendingCallHighWaterMarkForTesting => _activeConnection?.PendingCallHighWaterMark ?? 0;

    internal async ValueTask CloseActiveConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _activeConnection, null);
        if (connection is not null)
            await connection.DisposeAsync();
    }

    internal async ValueTask StopAndCloseActiveConnectionAsync()
    {
        await _connectionStateLock.WaitAsync();
        RpcConnection? connection;
        try
        {
            // Block concurrent reconnects before tearing the connection down.
            Volatile.Write(ref _stopping, 1);
            connection = Interlocked.Exchange(ref _activeConnection, null);
        }
        finally
        {
            _connectionStateLock.Release();
        }

        if (connection is not null)
            await connection.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _stopSource.Cancel();
        await StopAndCloseActiveConnectionAsync();
    }

    /// <summary>Decode a REPLY envelope, annotating failures with the CALL's program/procedure.</summary>
    internal static RpcReply DecodeReplyWithContext(
        byte[] reply,
        uint xid,
        uint program,
        uint version,
        uint procedure)
    {
        try
        {
            return RpcReplyParser.DecodeAccepted(reply, xid);
        }
        catch (NfsException ex)
        {
            throw new NfsException(
                $"RPC call failed (prog={program}, vers={version}, proc={procedure}): {ex.Message}",
                ex);
        }
    }

    /// <summary>Throw when an accepted reply is not SUCCESS, annotating the CALL's program/procedure.</summary>
    internal static void EnsureAcceptSuccess(RpcReply reply, uint program, uint version, uint procedure)
    {
        try
        {
            RpcReplyParser.ThrowIfAcceptFailed(reply);
        }
        catch (NfsException ex)
        {
            throw new NfsException(
                $"RPC call failed (prog={program}, vers={version}, proc={procedure}): {ex.Message}",
                ex);
        }
    }

    /// <summary>Build, send, and await a single CALL, correlating the REPLY by XID.</summary>
    private async Task<XdrReader> CallOnceAsync(
        RpcConnection connection,
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopSource.Token);
        var token = linkedCts.Token;
        var queuedAt = Stopwatch.GetTimestamp();
        await connection.WaitForCallSlotAsync(token);
        var queueDelay = Stopwatch.GetElapsedTime(queuedAt);
        // RPCSEC_GSS sequence numbers must be sent in order, so authenticated calls are serialized.
        var ownsGssLock = _gssSession.IsEstablished;
        var gssLockAcquired = false;
        uint xid = 0;
        TaskCompletionSource<byte[]>? pending = null;
        try
        {
            if (ownsGssLock)
            {
                await _gssCallLock.WaitAsync(token);
                gssLockAcquired = true;
            }

            using var timeoutCts = CreateCallTimeout(ct, out var callToken);
            // Register the waiter before writing so a fast REPLY cannot arrive unclaimed;
            // a rare XID collision with an in-flight call allocates another XID.
            do
            {
                xid = unchecked((uint)Interlocked.Increment(ref _xid));
            } while (!connection.TryRegister(xid, out pending));

            // CALL header (RFC 5531): xid, msg_type=CALL(0), rpcvers=2, then program, version, procedure.
            var writer = new XdrWriter();
            writer.UInt(xid);
            writer.UInt(0);
            writer.UInt(2);
            writer.UInt(program);
            writer.UInt(version);
            writer.UInt(procedure);

            // Credential then verifier: RPCSEC_GSS (RFC 2203) uses a per-attempt security
            // record and seals the header with a MIC; otherwise AUTH_SYS (flavor 1) with an
            // AUTH_NONE (flavor 0) verifier.
            RpcSecGssCallRecord? gssRecord = null;
            if (_gssSession.IsEstablished)
            {
                // Allocate the sequence number once per transmitted attempt, including retries.
                gssRecord = _gssSession.BeginDataCall(xid, program, version, procedure);
                writer.UInt((uint)RpcSecGssFlavor.Gss);
                _gssSession.WriteCredential(writer, gssRecord);
                // RFC 2203 §5.3.1: the header checksum covers the RPC header through the credential.
                var headerVerifier = _gssSession.CreateHeaderVerifier(writer.ToArray(), gssRecord);
                writer.UInt((uint)RpcSecGssFlavor.Gss);
                writer.Opaque(headerVerifier);
            }
            else
            {
                writer.UInt(1);
                writer.Opaque(_authSysBody);
                writer.UInt(0);
                writer.UInt(0);
            }

            writer.Raw(arguments);
            _logger?.LogDebug(
                "Sending RPC call (xid={Xid}, prog={Program}, vers={Version}, proc={Procedure}, generation={Generation}, queueMs={QueueMs}, pending={PendingCount}, pendingHighWater={PendingHighWater})",
                xid,
                program,
                version,
                procedure,
                connection.Generation,
                queueDelay.TotalMilliseconds,
                connection.PendingCallCount,
                connection.PendingCallHighWaterMark);
            var bytes = await connection.SendAndReceiveAsync(xid, pending, writer.ToArray(), callToken);
            var reply = DecodeReplyWithContext(bytes, xid, program, version, procedure);
            // Fail closed on RPCSEC_GSS verifier problems before any procedure result is exposed,
            // including accepted RPC errors whose verifier RFC 2203 still requires.
            if (gssRecord is not null)
                _gssSession.VerifyReply(reply, gssRecord);
            EnsureAcceptSuccess(reply, program, version, procedure);
            _logger?.LogDebug(
                "Received RPC reply (xid={Xid}, prog={Program}, vers={Version}, proc={Procedure}, generation={Generation}, pending={PendingCount}, pendingHighWater={PendingHighWater})",
                xid,
                program,
                version,
                procedure,
                connection.Generation,
                connection.PendingCallCount,
                connection.PendingCallHighWaterMark);
            return reply.Body;
        }
        finally
        {
            // Unregister the waiter first so the receive loop cannot complete a recycled XID late.
            if (pending is not null)
                connection.RemovePending(xid, pending);
            if (gssLockAcquired)
                _gssCallLock.Release();
            connection.ReleaseCallSlot();
        }
    }

    private RpcConnection RequireActiveConnection() =>
        _activeConnection ?? throw new NfsException("NFS connection is not established.");

    /// <summary>Return the active connection, reconnecting first if a receive failure marked it unhealthy.</summary>
    private async Task<RpcConnection> RequireHealthyConnectionAsync(CancellationToken ct)
    {
        var connection = RequireActiveConnection();
        if (connection.IsHealthy)
            return connection;

        // Unhealthy generation: swap in a fresh connection or surface the original failure.
        return await ReconnectAsync(connection, ct)
               ?? throw connection.Failure
                      ?? new NfsException($"RPC connection generation {connection.Generation} is unavailable.");
    }

    /// <summary>Replace a failed connection with a new generation, or return null when stopping.</summary>
    private async Task<RpcConnection?> ReconnectAsync(RpcConnection failedConnection, CancellationToken ct)
    {
        // Serialize reconnects so concurrent failures cannot open competing generations.
        await _connectionStateLock.WaitAsync(ct);
        try
        {
            // Stopping or never-connected clients have nothing to reconnect to.
            if (_activePort <= 0 || Volatile.Read(ref _stopping) != 0)
                return null;

            // Another caller may have already replaced the failed generation; reuse theirs.
            var active = _activeConnection;
            if (active is { IsHealthy: true } && !ReferenceEquals(active, failedConnection))
                return active;

            _logger?.LogInformation("Reconnecting to NFS server (port={Port})", _activePort);
            var previous = Interlocked.Exchange(ref _activeConnection, null);
            if (previous is not null)
            {
                try
                {
                    await previous.DisposeAsync();
                }
                catch
                {
                    // Continue with reconnect after best-effort cleanup.
                }
            }

            using var timeoutCts = CreateCallTimeout(ct, out var token);
            var connection = await _transport.OpenAsync(_activePort, token);
            // Install the new generation only after the open succeeds so callers never observe a half-built connection.
            _activeConnection = connection;
            _logger?.LogInformation(
                "Reconnected to NFS server (generation={Generation})",
                connection.Generation);
            return connection;
        }
        finally
        {
            _connectionStateLock.Release();
        }
    }

    private CancellationTokenSource? CreateCallTimeout(CancellationToken outer, out CancellationToken token)
    {
        if (_options.CommandTimeout <= TimeSpan.Zero)
        {
            token = outer;
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        source.CancelAfter(_options.CommandTimeout);
        token = source.Token;
        return source;
    }
}
