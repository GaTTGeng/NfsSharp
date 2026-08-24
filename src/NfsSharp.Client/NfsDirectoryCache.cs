using System.Collections.Concurrent;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

internal sealed class NfsDirectoryCache
{
    private readonly ConcurrentDictionary<byte[], Entry> _entries = new(ByteArrayComparer.Instance);
    private readonly bool _enabled;
    private readonly TimeSpan _ttl;
    private readonly object _mutationGate = new();
    private long _mutationGeneration;

    internal NfsDirectoryCache(NfsClientOptions options)
    {
        _enabled = options.EnableDirectoryCache;
        _ttl = options.DirectoryCacheTtl;
    }

    internal bool TryGet(byte[] directoryHandle, out List<NfsEntryPlus> entries)
    {
        entries = [];
        if (!_enabled || !_entries.TryGetValue(directoryHandle, out var cached))
            return false;

        if (DateTime.UtcNow >= cached.Expiry)
        {
            _entries.TryRemove(directoryHandle, out _);
            return false;
        }

        entries = Clone(cached.Entries);
        return true;
    }

    internal long CaptureMutationGeneration()
    {
        lock (_mutationGate)
            return _mutationGeneration;
    }

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

    private static List<NfsEntryPlus> Clone(IEnumerable<NfsEntryPlus> entries) =>
        entries.Select(entry => entry with { Handle = entry.Handle?.ToArray() }).ToList();

    private sealed record Entry(List<NfsEntryPlus> Entries, DateTime Expiry);

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
