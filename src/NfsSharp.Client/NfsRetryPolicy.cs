using System.Net.Sockets;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Decides which transient RPC failures may be retried and how long to wait.</summary>
internal sealed class NfsRetryPolicy
{
    private readonly NfsClientOptions _options;

    internal NfsRetryPolicy(NfsClientOptions options)
    {
        _options = options;
    }

    /// <summary>First attempt plus the configured number of retries.</summary>
    internal int MaxAttempts => Math.Max(1, _options.MaxRetries + 1);

    /// <summary>Fixed delay between attempts (no backoff).</summary>
    internal Task DelayAsync(CancellationToken ct) =>
        _options.RetryDelay > TimeSpan.Zero
            ? Task.Delay(_options.RetryDelay, ct)
            : Task.CompletedTask;

    /// <summary>Transport-level failures that are safe to treat as transient.</summary>
    internal static bool IsTransient(Exception ex) =>
        ex is SocketException or IOException or ObjectDisposedException ||
        ex is NfsException { InnerException: Exception inner } && IsTransient(inner);

    /// <summary>
    /// Only idempotent procedures may be replayed automatically; mutating ops
    /// (WRITE, REMOVE, RENAME, ...) must surface the failure to the caller.
    /// </summary>
    internal static bool CanRetry(uint program, uint version, uint procedure) =>
        (program, version, procedure) switch
        {
            (NfsRpcConstants.ProgPortmap, NfsRpcConstants.VerPortmap, NfsRpcConstants.PmapGetPort) => true,
            (NfsRpcConstants.ProgMount, NfsRpcConstants.VerMount,
                NfsRpcConstants.MountMnt or NfsRpcConstants.MountExport) => true,
            (NfsRpcConstants.ProgNfs, NfsRpcConstants.VerNfs,
                NfsRpcConstants.NfsGetAttr or NfsRpcConstants.NfsLookup or NfsRpcConstants.NfsAccess or
                NfsRpcConstants.NfsReadlink or NfsRpcConstants.NfsRead or NfsRpcConstants.NfsReadDir or
                NfsRpcConstants.NfsReadDirPlus or NfsRpcConstants.NfsFsstat or NfsRpcConstants.NfsFsinfo or
                NfsRpcConstants.NfsPathconf or NfsRpcConstants.NfsCommit) => true,
            _ => false
        };
}
