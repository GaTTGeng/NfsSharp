using NfsSharp.Protocol;

namespace NfsSharp.Client;

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

    internal async Task<NfsLookup> ResolveAsync(string path, CancellationToken ct)
    {
        var root = _rootHandle();
        var handle = root;
        NfsLookup? current = null;
        foreach (var part in Split(path))
        {
            current = await _lookup(handle, part, ct);
            handle = current.Handle;
        }

        return current ?? new NfsLookup(root, await _getAttributes(root, ct));
    }

    internal async Task<(byte[] ParentHandle, string Name)> ResolveParentAsync(
        string path,
        CancellationToken ct)
    {
        var parts = Split(path).ToArray();
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
