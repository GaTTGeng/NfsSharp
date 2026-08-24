using NfsSharp.Protocol;

namespace NfsSharp.Client;

public sealed partial class NfsV3Client
{
    /// <summary>Create a file and return its handle.</summary>
    public async Task<NfsLookup> CreateAndOpenFileAsync(
        string path, NfsSetAttributes? attributes, CancellationToken ct)
    {
        var (parent, name) = await _pathResolver.ResolveParentAsync(path, ct);
        return await CreateFileAsync(parent, name, attributes ?? NfsSetAttributes.FileDefault, ct);
    }

    /// <summary>COMMIT — flush cached data to stable storage for a file handle.</summary>
    public async Task CommitAsync(byte[] fileFh, ulong offset, uint count, CancellationToken ct) =>
        _ = await CommitWithResultAsync(fileFh, offset, count, ct);
    /// <summary>COMMIT and return the server write verifier.</summary>
    public Task<NfsCommitResult> CommitWithResultAsync(
        byte[] fileFh, ulong offset, uint count, CancellationToken ct) =>
        _protocolClient.CommitAsync(fileFh, offset, count, ct);
    /// <summary>COMMIT an export-relative file.</summary>
    public async Task CommitAsync(string path, ulong offset, uint count, CancellationToken ct) =>
        await CommitAsync((await LookupPathAsync(path, ct)).Handle, offset, count, ct);
    /// <summary>COMMIT an export-relative file and return the server write verifier.</summary>
    public async Task<NfsCommitResult> CommitWithResultAsync(
        string path, ulong offset, uint count, CancellationToken ct) =>
        await CommitWithResultAsync((await LookupPathAsync(path, ct)).Handle, offset, count, ct);

    /// <summary>SYMLINK — create a symbolic link in a directory handle.</summary>
    public Task<NfsLookup> CreateSymLinkAsync(
        byte[] dirFh, string linkName, string targetPath, NfsSetAttributes? attributes, CancellationToken ct) =>
        _protocolClient.CreateSymbolicLinkAsync(dirFh, linkName, targetPath, attributes, ct);
    /// <summary>SYMLINK — create a symbolic link at an export-relative path.</summary>
    public async Task<NfsLookup> CreateSymLinkAsync(string linkPath, string targetPath, CancellationToken ct)
    {
        var (parent, name) = await _pathResolver.ResolveParentAsync(linkPath, ct);
        return await CreateSymLinkAsync(parent, name, targetPath, null, ct);
    }

    /// <summary>LINK — create a hard link in a directory.</summary>
    public Task CreateHardLinkAsync(
        byte[] targetFh, byte[] linkDirFh, string linkName, CancellationToken ct) =>
        _protocolClient.CreateHardLinkAsync(targetFh, linkDirFh, linkName, ct);
    /// <summary>LINK — create a hard link at an export-relative path.</summary>
    public async Task CreateHardLinkAsync(string existingFilePath, string linkPath, CancellationToken ct)
    {
        var target = await LookupPathAsync(existingFilePath, ct);
        var (directory, name) = await _pathResolver.ResolveParentAsync(linkPath, ct);
        await CreateHardLinkAsync(target.Handle, directory, name, ct);
    }

    /// <summary>MKNOD — create a device node, FIFO, or socket.</summary>
    public Task<NfsLookup> CreateNodeAsync(
        byte[] dirFh, string name, NfsType type, NfsSetAttributes? attributes,
        uint? majorDevice, uint? minorDevice, CancellationToken ct) =>
        _protocolClient.CreateNodeAsync(dirFh, name, type, attributes, majorDevice, minorDevice, ct);

    /// <summary>READDIRPLUS for a directory handle.</summary>
    public Task<List<NfsEntryPlus>> ReadDirPlusAsync(byte[] dirFh, CancellationToken ct) =>
        _protocolClient.ReadDirectoryPlusAsync(dirFh, ct);
    /// <summary>READDIRPLUS for an export-relative path.</summary>
    public async Task<List<NfsEntryPlus>> ReadDirPlusAsync(string path, CancellationToken ct) =>
        await ReadDirPlusAsync((await LookupPathAsync(path, ct)).Handle, ct);
    /// <summary>READDIR for a directory handle.</summary>
    public Task<List<NfsEntry>> ReadDirAsync(byte[] dirFh, CancellationToken ct) =>
        _protocolClient.ReadDirectoryAsync(dirFh, ct);
    /// <summary>READDIR for an export-relative path.</summary>
    public async Task<List<NfsEntry>> ReadDirAsync(string path, CancellationToken ct) =>
        await ReadDirAsync((await LookupPathAsync(path, ct)).Handle, ct);
    /// <summary>Alias for callers expecting item-list terminology.</summary>
    public Task<List<NfsEntry>> GetItemListAsync(string path, CancellationToken ct) => ReadDirAsync(path, ct);

    /// <summary>CHMOD — set file mode on a file handle.</summary>
    public Task ChmodAsync(byte[] fileHandle, uint mode, CancellationToken ct) =>
        SetAttributesAsync(fileHandle, new NfsSetAttributes { Mode = mode }, ct);
    /// <summary>CHMOD for an export-relative path.</summary>
    public async Task ChmodAsync(string path, uint mode, CancellationToken ct) =>
        await ChmodAsync((await LookupPathAsync(path, ct)).Handle, mode, ct);
    /// <summary>CHOWN — set uid/gid on a file handle.</summary>
    public Task ChownAsync(byte[] fileHandle, uint uid, uint gid, CancellationToken ct) =>
        SetAttributesAsync(fileHandle, new NfsSetAttributes { Uid = uid, Gid = gid }, ct);
    /// <summary>CHOWN for an export-relative path.</summary>
    public async Task ChownAsync(string path, uint uid, uint gid, CancellationToken ct) =>
        await ChownAsync((await LookupPathAsync(path, ct)).Handle, uid, gid, ct);
    /// <summary>UTIMES — set access and modification times on a file handle.</summary>
    public Task UtimesAsync(
        byte[] fileHandle, DateTime? atime, DateTime? mtime, CancellationToken ct) =>
        SetAttributesAsync(fileHandle, new NfsSetAttributes { Atime = atime, Mtime = mtime }, ct);
    /// <summary>UTIMES for an export-relative path.</summary>
    public async Task UtimesAsync(
        string path, DateTime? atime, DateTime? mtime, CancellationToken ct) =>
        await UtimesAsync((await LookupPathAsync(path, ct)).Handle, atime, mtime, ct);

    /// <summary>READ a file handle at a specific offset.</summary>
    public async Task<(int BytesRead, bool Eof)> ReadAtAsync(
        byte[] fileFh, ulong offset, byte[] buffer, int bufferOffset, int count, CancellationToken ct)
    {
        NfsV3ProtocolClient.ValidateHandle(fileFh);
        ArgumentNullException.ThrowIfNull(buffer);
        ValidateBufferRange(buffer.Length, bufferOffset, count);
        return await _protocolClient.ReadAsync(fileFh, offset, buffer.AsMemory(bufferOffset, count), ct);
    }

    /// <summary>WRITE to a file handle at a specific offset.</summary>
    public async Task<int> WriteAtAsync(
        byte[] fileFh, ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        (await WriteAtWithResultAsync(fileFh, offset, data, ct)).Count;
    /// <summary>WRITE and return count, stability, and verifier.</summary>
    public Task<NfsWriteResult> WriteAtWithResultAsync(
        byte[] fileFh, ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _protocolClient.WriteAsync(fileFh, offset, data, ct);

    /// <summary>READ a file handle into a stream until EOF.</summary>
    public async Task ReadFileAsync(byte[] fileFh, Stream output, CancellationToken ct)
    {
        ValidateWritableStream(output);
        NfsV3ProtocolClient.ValidateHandle(fileFh);
        var buffer = new byte[_options.MaxReadSize];
        ulong offset = 0;
        while (true)
        {
            var (count, eof) = await _protocolClient.ReadAsync(fileFh, offset, buffer, ct);
            if (count > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                offset += (ulong)count;
            }
            if (eof) break;
            if (count == 0) throw new NfsException("READ returned a non-terminal response without data.");
        }
    }

    /// <summary>READ an export-relative path into a stream.</summary>
    public async Task ReadFileAsync(string remotePath, Stream output, CancellationToken ct)
    {
        ValidateWritableStream(output);
        await ReadFileAsync((await OpenFileAsync(remotePath, ct)).Handle, output, ct);
    }

    /// <summary>READ an export-relative path into a local file.</summary>
    public async Task ReadFileAsync(string remotePath, string localPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullPath = Path.GetFullPath(localPath);
        var item = await OpenFileAsync(remotePath, ct);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var output = File.Create(fullPath);
        await ReadFileAsync(item.Handle, output, ct);
    }

    /// <summary>WRITE stream content to an existing file handle.</summary>
    public async Task WriteFileAsync(byte[] fileFh, Stream input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateReadableStream(input);
        NfsV3ProtocolClient.ValidateHandle(fileFh);
        var buffer = new byte[_options.MaxWriteSize];
        ulong offset = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            var written = 0;
            while (written < read)
            {
                var result = await _protocolClient.WriteAsync(
                    fileFh, offset, buffer.AsMemory(written, read - written), ct);
                if (result.Count <= 0) throw new NfsException("WRITE made no progress.");
                written += result.Count;
                offset += (ulong)result.Count;
            }
        }
    }

    /// <summary>Create or truncate a file, then write stream content.</summary>
    public async Task<NfsLookup> WriteFileAsync(string remotePath, Stream input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateReadableStream(input);
        var (parent, name) = await _pathResolver.ResolveParentAsync(remotePath, ct);
        NfsLookup file;
        try
        {
            file = await LookupAsync(parent, name, ct);
            await SetFileSizeAsync(file.Handle, 0, ct);
        }
        catch (NfsException ex) when (ex.IsNotFound)
        {
            file = await CreateFileAsync(parent, name, NfsSetAttributes.FileDefault, ct);
        }
        await WriteFileAsync(file.Handle, input, ct);
        return file with { Attr = await GetAttributesAsync(file.Handle, ct) };
    }

    /// <summary>WRITE a local file to an export-relative path.</summary>
    public async Task<NfsLookup> WriteFileAsync(string remotePath, string localPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        await using var input = File.OpenRead(localPath);
        return await WriteFileAsync(remotePath, input, ct);
    }

    /// <summary>CREATE a file in a directory handle.</summary>
    public Task<NfsLookup> CreateFileAsync(
        byte[] dirFh, string name, NfsSetAttributes? attributes, CancellationToken ct) =>
        _protocolClient.CreateFileAsync(dirFh, name, attributes, ct);
    /// <summary>CREATE an export-relative file.</summary>
    public async Task<NfsLookup> CreateFileAsync(string path, CancellationToken ct)
    {
        var (parent, name) = await _pathResolver.ResolveParentAsync(path, ct);
        return await CreateFileAsync(parent, name, NfsSetAttributes.FileDefault, ct);
    }

    /// <summary>MKDIR in a directory handle.</summary>
    public Task<NfsLookup> CreateDirectoryAsync(
        byte[] dirFh, string name, NfsSetAttributes? attributes, CancellationToken ct) =>
        _protocolClient.CreateDirectoryAsync(dirFh, name, attributes, ct);
    /// <summary>MKDIR for an export-relative path.</summary>
    public async Task<NfsLookup> CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var (parent, name) = await _pathResolver.ResolveParentAsync(path, ct);
        return await CreateDirectoryAsync(parent, name, NfsSetAttributes.DirectoryDefault, ct);
    }

    /// <summary>SETATTR size for an existing file handle.</summary>
    public Task SetFileSizeAsync(byte[] fileFh, ulong size, CancellationToken ct) =>
        SetAttributesAsync(fileFh, new NfsSetAttributes { Size = size }, ct);
    /// <summary>SETATTR size for an export-relative path.</summary>
    public async Task SetFileSizeAsync(string path, ulong size, CancellationToken ct) =>
        await SetFileSizeAsync((await LookupPathAsync(path, ct)).Handle, size, ct);
    /// <summary>SETATTR for an existing file handle.</summary>
    public Task SetAttributesAsync(byte[] fileFh, NfsSetAttributes attributes, CancellationToken ct) =>
        _protocolClient.SetAttributesAsync(fileFh, attributes, null, ct);
    /// <summary>Guarded SETATTR for an existing file handle.</summary>
    public Task SetAttributesGuardedAsync(
        byte[] fileFh, NfsSetAttributes attributes, NfsTimestamp guardCtime, CancellationToken ct) =>
        _protocolClient.SetAttributesAsync(fileFh, attributes, guardCtime, ct);
    /// <summary>SETATTR for an export-relative path.</summary>
    public async Task SetAttributesAsync(string path, NfsSetAttributes attributes, CancellationToken ct) =>
        await SetAttributesAsync((await LookupPathAsync(path, ct)).Handle, attributes, ct);
    /// <summary>Guarded SETATTR for an export-relative path.</summary>
    public async Task SetAttributesGuardedAsync(
        string path, NfsSetAttributes attributes, NfsTimestamp guardCtime, CancellationToken ct) =>
        await SetAttributesGuardedAsync((await LookupPathAsync(path, ct)).Handle, attributes, guardCtime, ct);

    /// <summary>REMOVE an export-relative file.</summary>
    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        var (parent, name) = await _pathResolver.ResolveParentAsync(path, ct);
        await RemoveAsync(NfsRpcConstants.NfsRemove, parent, name, $"REMOVE \"{name}\" failed", ct);
    }

    /// <summary>RMDIR an export-relative directory.</summary>
    public async Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct)
    {
        var (parent, name) = await _pathResolver.ResolveParentAsync(path, ct);
        await DeleteDirectoryAsync(parent, name, recursive, ct);
    }
    /// <summary>Recursively RMDIR an export-relative directory.</summary>
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => DeleteDirectoryAsync(path, true, ct);

    /// <summary>RENAME/MOVE an export-relative path.</summary>
    public async Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        var (sourceParent, sourceName) = await _pathResolver.ResolveParentAsync(sourcePath, ct);
        var (targetParent, targetName) = await _pathResolver.ResolveParentAsync(targetPath, ct);
        await _protocolClient.RenameAsync(
            sourceParent, sourceName, targetParent, targetName,
            $"RENAME \"{sourcePath}\" to \"{targetPath}\" failed", ct);
    }

    private async Task DeleteDirectoryAsync(
        byte[] parentHandle, string name, bool recursive, CancellationToken ct)
    {
        if (recursive)
        {
            var directory = await LookupAsync(parentHandle, name, ct);
            foreach (var entry in await ReadDirAsync(directory.Handle, ct))
            {
                if (entry.Name is "." or "..") continue;
                var child = await LookupAsync(directory.Handle, entry.Name, ct);
                if (child.Attr?.Type == NfsType.Dir)
                    await DeleteDirectoryAsync(directory.Handle, entry.Name, true, ct);
                else
                    await RemoveAsync(NfsRpcConstants.NfsRemove, directory.Handle, entry.Name,
                        $"REMOVE \"{entry.Name}\" failed", ct);
            }
        }
        await RemoveAsync(NfsRpcConstants.NfsRmdir, parentHandle, name, $"RMDIR \"{name}\" failed", ct);
    }

    private Task RemoveAsync(uint procedure, byte[] parent, string name, string message, CancellationToken ct) =>
        _protocolClient.RemoveAsync(procedure, parent, name, message, ct);

    private static void ValidateBufferRange(int length, int offset, int count)
    {
        if (offset < 0) throw new NfsException("Buffer offset cannot be negative.");
        if (count < 0) throw new NfsException("Count cannot be negative.");
        if (offset > length || count > length - offset)
            throw new NfsException("Buffer offset and count exceed the buffer length.");
    }

    private static void ValidateReadableStream(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new NfsException("Input stream must be readable.");
    }

    private static void ValidateWritableStream(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new NfsException("Output stream must be writable.");
    }
}
