using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>
/// Resolves export-relative paths to NFS file handles by walking components with LOOKUP,
/// always starting from the mounted export root.
/// </summary>
internal sealed class NfsPathResolver
{
    private readonly Func<byte[]> _rootHandle;
    private readonly Func<byte[], string, CancellationToken, Task<NfsLookup>> _lookup;
    private readonly Func<byte[], CancellationToken, Task<NfsFattr>> _getAttributes;

    internal NfsPathResolver(
        Func<byte[]> rootHandle,
        Func<byte[], string, CancellationToken, Task<NfsLookup>> lookup,
        Func<byte[], CancellationToken, Task<NfsFattr>> getAttributes)
    {
        _rootHandle = rootHandle;
        _lookup = lookup;
        _getAttributes = getAttributes;
    }

    /// <summary>Resolve a full path; empty or "." resolves to the export root itself.</summary>
    internal async Task<NfsLookup> ResolveAsync(string path, CancellationToken ct)
    {
        var root = _rootHandle();
        var handle = root;
        NfsLookup? current = null;
        // LOOKUP each component in order; NFSv3 has no single-call path walk.
        foreach (var part in Split(path))
        {
            current = await _lookup(handle, part, ct);
            handle = current.Handle;
        }

        return current ?? new NfsLookup(root, await _getAttributes(root, ct));
    }

    /// <summary>Resolve the parent directory handle and final name for CREATE/REMOVE/RENAME-style calls.</summary>
    internal async Task<(byte[] ParentHandle, string Name)> ResolveParentAsync(
        string path,
        CancellationToken ct)
    {
        var parts = Split(path).ToArray();
        // The export root has no parent handle/name pair to operate on.
        if (parts.Length == 0)
            throw new NfsException("Path must point to an item below the export root.");

        var parent = _rootHandle();
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var lookup = await _lookup(parent, parts[index], ct);
            if (lookup.Attr?.Type != NfsType.Dir)
            {
                throw new NfsException(
                    $"Path component is not a directory: {parts[index]}",
                    NfsV3Status.NotDir);
            }

            parent = lookup.Handle;
        }

        return (parent, parts[^1]);
    }

    /// <summary>Split and validate path components; rejects ".." so callers cannot leave the export root.</summary>
    internal static IEnumerable<string> Split(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "/")
            yield break;

        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
                throw new NfsException("Parent path traversal is not allowed.");

            ValidateName(part);
            yield return part;
        }
    }

    /// <summary>Validate one path component (no separators, no dot names, at most 255 chars).</summary>
    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new NfsException("NFS path component is empty.");
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            throw new NfsException($"Invalid NFS path component: {name}");
        if (name.Length > 255)
            throw new NfsException($"NFS path component is too long: {name}");
    }
}
