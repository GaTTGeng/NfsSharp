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
    private bool _usePrivilegedSourcePort = true;
    private int _portmapPort = 111;
    private int _maxReadSize = 128 * 1024;
    private int _maxWriteSize = 128 * 1024;
    private int _readdirCount = 32 * 1024;
    private NfsWriteStableHow _stableHow = NfsWriteStableHow.FileSync;
    private int _maxRetries = 2;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
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

    public NfsClientBuilder WithPrivilegedSourcePort(bool enabled)
    {
        _usePrivilegedSourcePort = enabled;
        return this;
    }

    public NfsClientBuilder WithPortmapPort(int port)
    {
        _portmapPort = port;
        return this;
    }

    public NfsClientBuilder WithMaxReadSize(int size)
    {
        _maxReadSize = size;
        return this;
    }

    public NfsClientBuilder WithMaxWriteSize(int size)
    {
        _maxWriteSize = size;
        return this;
    }

    public NfsClientBuilder WithReaddirCount(int count)
    {
        _readdirCount = count;
        return this;
    }

    public NfsClientBuilder WithWriteStability(NfsWriteStableHow stableHow)
    {
        _stableHow = stableHow;
        return this;
    }

    public NfsClientBuilder WithMaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;
        return this;
    }

    public NfsClientBuilder WithRetryDelay(TimeSpan delay)
    {
        _retryDelay = delay;
        return this;
    }

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

    public NfsClientBuilder WithKerberos(string targetSpn, RpcSecGssService service = RpcSecGssService.Integrity)
    {
        _gssMechanism = new NegotiateGssMechanism("Kerberos");
        _gssService = service;
        _gssTargetName = targetSpn;
        return this;
    }

    public NfsClientBuilder WithGssMechanism(IRpcSecGssMechanism mechanism, RpcSecGssService service = RpcSecGssService.Integrity)
    {
        _gssMechanism = mechanism;
        _gssService = service;
        return this;
    }

    public NfsClientBuilder WithGssCredentials(GssCredentials credentials)
    {
        _gssCredentials = credentials;
        return this;
    }

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

    public NfsClient Build() => new(_version, BuildOptions());
}
