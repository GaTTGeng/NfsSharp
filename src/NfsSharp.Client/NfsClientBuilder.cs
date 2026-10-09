using Microsoft.Extensions.Logging;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>
/// Fluent builder for configuring and creating <see cref="NfsClient"/> instances.
/// </summary>
public sealed class NfsClientBuilder
{
    private NfsVersion _version = NfsVersion.V3;
    private uint _userId;
    private uint _groupId;
    private IReadOnlyList<uint> _auxiliaryGroups = Array.Empty<uint>();
    private TimeSpan _commandTimeout = TimeSpan.FromSeconds(30);
    // Privileged source ports are still commonly required by mountd on traditional servers.
    private bool _usePrivilegedSourcePort = true;
    private int _portmapPort = 111;
    private int _maxReadSize = 128 * 1024;
    private int _maxWriteSize = 128 * 1024;
    // READDIR/READDIRPLUS maxcount per request; larger values fetch more entries per round trip.
    private int _readdirCount = 32 * 1024;
    // FileSync trades throughput for durability; most callers want writes stable after WRITE returns.
    private NfsWriteStableHow _stableHow = NfsWriteStableHow.FileSync;
    private int _maxRetries = 2;
    private int _maxOutstandingRpcCallsPerConnection = 32;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
    // Directory caching is opt-in because it can serve stale listings within the TTL.
    private bool _enableDirectoryCache;
    private TimeSpan _directoryCacheTtl = TimeSpan.FromSeconds(30);
    private bool _tcpKeepAlive = true;
    private TimeSpan _keepAliveInterval = TimeSpan.FromSeconds(30);
    private bool _tcpNoDelay = true;
    private ILogger? _logger;
    private IRpcSecGssMechanism? _gssMechanism;
    private RpcSecGssService _gssService = RpcSecGssService.Integrity;
    private GssCredentials? _gssCredentials;
    private string? _gssTargetName;

    /// <summary>Select the NFS protocol version. Only <see cref="NfsVersion.V3"/> is currently supported.</summary>
    public NfsClientBuilder WithVersion(NfsVersion version)
    {
        _version = version;
        return this;
    }

    public NfsClientBuilder WithCredentials(uint userId, uint groupId)
    {
        _userId = userId;
        _groupId = groupId;
        return this;
    }

    public NfsClientBuilder WithCredentials(uint userId, uint groupId, IReadOnlyList<uint> auxiliaryGroups)
    {
        _userId = userId;
        _groupId = groupId;
        _auxiliaryGroups = auxiliaryGroups;
        return this;
    }

    public NfsClientBuilder WithCommandTimeout(TimeSpan timeout)
    {
        _commandTimeout = timeout;
        return this;
    }

    /// <summary>Bind the RPC client to a source port below 1024 (may require elevated privileges).</summary>
    public NfsClientBuilder WithPrivilegedSourcePort(bool enabled)
    {
        _usePrivilegedSourcePort = enabled;
        return this;
    }

    /// <summary>Set the portmapper port (default 111).</summary>
    public NfsClientBuilder WithPortmapPort(int port)
    {
        _portmapPort = port;
        return this;
    }

    /// <summary>Maximum READ payload per request, in bytes.</summary>
    public NfsClientBuilder WithMaxReadSize(int size)
    {
        _maxReadSize = size;
        return this;
    }

    /// <summary>Maximum WRITE payload per request, in bytes.</summary>
    public NfsClientBuilder WithMaxWriteSize(int size)
    {
        _maxWriteSize = size;
        return this;
    }

    /// <summary>READDIR/READDIRPLUS maxcount per request, in bytes.</summary>
    public NfsClientBuilder WithReaddirCount(int count)
    {
        _readdirCount = count;
        return this;
    }

    /// <summary>Default stability level requested by WRITE.</summary>
    public NfsClientBuilder WithWriteStability(NfsWriteStableHow stableHow)
    {
        _stableHow = stableHow;
        return this;
    }

    /// <summary>Maximum transient-failure retries applied to idempotent RPCs.</summary>
    public NfsClientBuilder WithMaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;
        return this;
    }

    /// <summary>Sets the maximum number of simultaneous RPC calls on one TCP connection.</summary>
    public NfsClientBuilder WithMaxOutstandingRpcCallsPerConnection(int maxCalls)
    {
        _maxOutstandingRpcCallsPerConnection = maxCalls;
        return this;
    }

    /// <summary>Delay before each transient-failure retry.</summary>
    public NfsClientBuilder WithRetryDelay(TimeSpan delay)
    {
        _retryDelay = delay;
        return this;
    }

    /// <summary>Enable READDIRPLUS result caching with an optional TTL (default 30 s).</summary>
    public NfsClientBuilder WithDirectoryCache(bool enabled, TimeSpan? ttl = null)
    {
        _enableDirectoryCache = enabled;
        if (ttl.HasValue)
            _directoryCacheTtl = ttl.Value;
        return this;
    }

    public NfsClientBuilder WithTcpKeepAlive(bool enabled, TimeSpan? interval = null)
    {
        _tcpKeepAlive = enabled;
        if (interval.HasValue)
            _keepAliveInterval = interval.Value;
        return this;
    }

    public NfsClientBuilder WithTcpNoDelay(bool enabled)
    {
        _tcpNoDelay = enabled;
        return this;
    }

    public NfsClientBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Use Kerberos via RPCSEC_GSS against the given target SPN.
    /// Experimental: <see cref="NegotiateGssMechanism"/> currently performs token exchange only.
    /// Integrity and privacy service are rejected until GSS MIC/wrap operations are implemented.
    /// </summary>
    public NfsClientBuilder WithKerberos(string targetSpn, RpcSecGssService service = RpcSecGssService.Integrity)
    {
        _gssMechanism = new NegotiateGssMechanism("Kerberos");
        _gssService = service;
        _gssTargetName = targetSpn;
        return this;
    }

    /// <summary>Use a custom RPCSEC_GSS mechanism instead of AUTH_SYS.</summary>
    public NfsClientBuilder WithGssMechanism(IRpcSecGssMechanism mechanism, RpcSecGssService service = RpcSecGssService.Integrity)
    {
        _gssMechanism = mechanism;
        _gssService = service;
        return this;
    }

    /// <summary>Set explicit GSS credentials (otherwise the process identity is used).</summary>
    public NfsClientBuilder WithGssCredentials(GssCredentials credentials)
    {
        _gssCredentials = credentials;
        return this;
    }

    /// <summary>Materialize the configured <see cref="NfsClientOptions"/> without creating a client.</summary>
    public NfsClientOptions BuildOptions() => new()
    {
        UserId = _userId,
        GroupId = _groupId,
        AuxiliaryGroups = _auxiliaryGroups,
        CommandTimeout = _commandTimeout,
        UsePrivilegedSourcePort = _usePrivilegedSourcePort,
        PortmapPort = _portmapPort,
        MaxReadSize = _maxReadSize,
        MaxWriteSize = _maxWriteSize,
        ReaddirCount = _readdirCount,
        StableHow = _stableHow,
        MaxRetries = _maxRetries,
        MaxOutstandingRpcCallsPerConnection = _maxOutstandingRpcCallsPerConnection,
        RetryDelay = _retryDelay,
        EnableDirectoryCache = _enableDirectoryCache,
        DirectoryCacheTtl = _directoryCacheTtl,
        TcpKeepAlive = _tcpKeepAlive,
        KeepAliveInterval = _keepAliveInterval,
        TcpNoDelay = _tcpNoDelay,
        Logger = _logger,
        GssMechanism = _gssMechanism,
        GssService = _gssService,
        GssCredentials = _gssCredentials,
        GssTargetName = _gssTargetName
    };

    /// <summary>Create a <see cref="NfsClient"/> with the configured options.</summary>
    public NfsClient Build() => new(_version, BuildOptions());
}
