using System.Collections.Concurrent;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>
/// Optional TTL cache of READDIRPLUS results keyed by directory handle.
/// Disabled unless <see cref="NfsClientOptions.EnableDirectoryCache"/> is set.
/// </summary>
internal sealed class NfsDirectoryCache
{
    private readonly ConcurrentDictionary<byte[], Entry> _entries = new(ByteArrayComparer.Instance);
    private readonly bool _enabled;
    private readonly TimeSpan _ttl;
    private readonly object _mutationGate = new();
    // Bumped on every mutation invalidation; lets Store() drop results read across a mutation.
    private long _mutationGeneration;

    internal NfsDirectoryCache(NfsClientOptions options)
    {
        _enabled = options.EnableDirectoryCache;
        _ttl = options.DirectoryCacheTtl;
    }

    /// <summary>Return a cloned cached listing when present and not expired.</summary>
    internal bool TryGet(byte[] directoryHandle, out List<NfsEntryPlus> entries)
    {
        entries = [];
        if (!_enabled || !_entries.TryGetValue(directoryHandle, out var cached))
            return false;

        // Lazy TTL expiry: a stale entry is dropped on read instead of by a timer.
        if (DateTime.UtcNow >= cached.Expiry)
        {
            _entries.TryRemove(directoryHandle, out _);
            return false;
        }

        entries = Clone(cached.Entries);
        return true;
    }

    /// <summary>Snapshot the mutation generation before a directory read begins.</summary>
    internal long CaptureMutationGeneration()
    {
        lock (_mutationGate)
            return _mutationGeneration;
    }

    /// <summary>Cache a listing only if no mutation happened since the generation was captured.</summary>
    internal void Store(
        byte[] directoryHandle,
        IEnumerable<NfsEntryPlus> entries,
        long observedMutationGeneration)
    {
        if (!_enabled)
            return;

        var entry = new Entry(Clone(entries), DateTime.UtcNow.Add(_ttl));
        lock (_mutationGate)
        {
            if (observedMutationGeneration != _mutationGeneration)
                return;
            _entries[directoryHandle.ToArray()] = entry;
        }
    }

    /// <summary>Invalidate the cached listing of one directory and bump the mutation generation.</summary>
    internal void Invalidate(byte[] directoryHandle)
    {
        if (_enabled)
        {
            lock (_mutationGate)
            {
                _mutationGeneration++;
                _entries.TryRemove(directoryHandle, out _);
            }
        }
    }

    /// <summary>
    /// Invalidate after a mutation of the object with the given handle: its own directory listing
    /// and any cached listing that contains it as an entry (attributes there can now be stale).
    /// </summary>
    internal void InvalidateForMutation(byte[] handle)
    {
        if (!_enabled)
            return;

        lock (_mutationGate)
        {
            _mutationGeneration++;
            if (_entries.IsEmpty)
                return;

            _entries.TryRemove(handle, out _);
            foreach (var cached in _entries)
            {
                if (cached.Value.Entries.Any(
                        entry => entry.Handle is not null && entry.Handle.AsSpan().SequenceEqual(handle)))
                {
                    _entries.TryRemove(cached.Key, out _);
                }
            }
        }
    }

    /// <summary>Deep-copy entries and handles so callers cannot mutate cached state.</summary>
    private static List<NfsEntryPlus> Clone(IEnumerable<NfsEntryPlus> entries) =>
        entries.Select(entry => entry with { Handle = entry.Handle?.ToArray() }).ToList();

    private sealed record Entry(List<NfsEntryPlus> Entries, DateTime Expiry);

    /// <summary>Content-based equality/hashing so equal handle byte sequences share a cache slot.</summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            return left is not null && right is not null && left.AsSpan().SequenceEqual(right);
        }

        public int GetHashCode(byte[] value)
        {
            var hash = new HashCode();
            hash.AddBytes(value);
            return hash.ToHashCode();
        }
    }
}
