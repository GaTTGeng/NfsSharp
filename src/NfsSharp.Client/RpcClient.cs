using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal interface IRpcCallClient
{
    Task<XdrReader> CallAsync(
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct);
}

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
            logger: options.Logger);
    }

    internal async Task ConnectAsync(int port, CancellationToken ct)
    {
        var connection = await _transport.OpenAsync(port, ct);
        var previous = Interlocked.Exchange(ref _activeConnection, connection);
        _activePort = port;
        if (previous is not null)
            await previous.DisposeAsync();
    }

    public async Task<XdrReader> CallAsync(
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct)
    {
        var connection = RequireActiveConnection();
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
                _logger?.LogWarning(
                    ex,
                    "RPC call failed transiently (attempt {Attempt}/{MaxAttempts}, prog={Program}, proc={Procedure})",
                    attempt,
                    _retryPolicy.MaxAttempts,
                    program,
                    procedure);
                connection = await ReconnectAsync(connection, ct) ?? connection;
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
        }

        throw new NfsException("RPC call failed after all retry attempts.");
    }

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
                if (connection is not null)
                    await connection.DisposeAsync();
            }
        }

        throw new NfsException("RPC call failed after all retry attempts.");
    }

    internal Task<XdrReader> CallRawAsync(
        uint program,
        uint version,
        uint procedure,
        byte[] arguments,
        CancellationToken ct) =>
        CallOnceAsync(RequireActiveConnection(), program, version, procedure, arguments, ct);

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

    internal static RpcReply DecodeReplyWithContext(
        byte[] reply,
        uint xid,
        uint program,
        uint version,
        uint procedure)
    {
        try
        {
            return RpcReplyParser.Decode(reply, xid);
        }
        catch (NfsException ex)
        {
            throw new NfsException(
                $"RPC call failed (prog={program}, vers={version}, proc={procedure}): {ex.Message}",
                ex);
        }
    }

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
            do
            {
                xid = unchecked((uint)Interlocked.Increment(ref _xid));
            } while (!connection.TryRegister(xid, out pending));

            var writer = new XdrWriter();
            writer.UInt(xid);
            writer.UInt(0);
            writer.UInt(2);
            writer.UInt(program);
            writer.UInt(version);
            writer.UInt(procedure);

            if (_gssSession.IsEstablished)
            {
                writer.UInt((uint)RpcSecGssFlavor.Gss);
                _gssSession.WriteCredential(writer);
                writer.UInt((uint)RpcSecGssFlavor.Gss);
                _gssSession.WriteVerifier(writer, arguments);
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
            _gssSession.ObserveReply(reply);
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
            if (pending is not null)
                connection.RemovePending(xid, pending);
            if (gssLockAcquired)
                _gssCallLock.Release();
            connection.ReleaseCallSlot();
        }
    }

    private RpcConnection RequireActiveConnection() =>
        _activeConnection ?? throw new NfsException("NFS connection is not established.");

    private async Task<RpcConnection?> ReconnectAsync(RpcConnection failedConnection, CancellationToken ct)
    {
        await _connectionStateLock.WaitAsync(ct);
        try
        {
            if (_activePort <= 0 || Volatile.Read(ref _stopping) != 0)
                return null;

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
