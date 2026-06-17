using System.Net;
using NfsSharp.Protocol;

namespace NfsSharp.Client;

/// <summary>Protocol versions accepted by the high-level <see cref="NfsClient"/> facade.</summary>
public enum NfsVersion
{
    V2 = 2,
    V3 = 3,
    V41 = 41
}

/// <summary>
/// Stateful convenience facade over <see cref="NfsV3Client"/> with terminology close to
/// common NFS client libraries: connect, list exports, mount, operate, unmount.
/// </summary>
public sealed class NfsClient : IAsyncDisposable
{
    private readonly NfsVersion _version;
    private readonly NfsClientOptions _options;
    private string? _server;
    private NfsV3Client? _mounted;

    public NfsClient(NfsVersion version = NfsVersion.V3, NfsClientOptions? options = null)
    {
        _version = version;
        _options = options ?? NfsClientOptions.Default;
        EnsureSupportedVersion(version);
        _options.Validate();
    }

    public bool IsConnected => !string.IsNullOrWhiteSpace(_server);

    public bool IsMounted => _mounted is not null;

    public byte[] RootHandle => RequireMounted().RootHandle;

    /// <summary>Store the server address for later export listing or mounting.</summary>
    public Task ConnectAsync(string server, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new NfsException("NFS server is empty.");

        _server = server;
        return Task.CompletedTask;
    }

    /// <summary>Store the server address for later export listing or mounting.</summary>
    public Task ConnectAsync(IPAddress server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return ConnectAsync(server.ToString(), ct);
    }

    /// <summary>List exports from the connected server.</summary>
    public Task<IReadOnlyList<NfsExport>> GetExportedDevicesAsync(CancellationToken ct = default) =>
        NfsV3Client.ListExportsAsync(RequireServer(), _options, ct);

    /// <summary>Mount an export from the connected server.</summary>
    public async Task MountDeviceAsync(string exportPath, CancellationToken ct = default)
    {
        if (_mounted is not null)
            await UnMountDeviceAsync(ct);

        _mounted = await NfsV3Client.ConnectAsync(RequireServer(), exportPath, _options, ct);
    }

    /// <summary>Unmount the current export.</summary>
    public async Task UnMountDeviceAsync(CancellationToken ct = default)
    {
        if (_mounted is null)
            return;

        await _mounted.UnmountAsync(ct);
        await _mounted.DisposeAsync();
        _mounted = null;
    }

    public Task<NfsLookup> LookupAsync(string path, CancellationToken ct = default) =>
        RequireMounted().LookupPathAsync(path, ct);

    public Task<NfsLookup> OpenFileAsync(string path, CancellationToken ct = default) =>
        RequireMounted().OpenFileAsync(path, ct);

    public Task<NfsLookup> CreateAndOpenFileAsync(string path, NfsSetAttributes? attributes = null, CancellationToken ct = default) =>
        RequireMounted().CreateAndOpenFileAsync(path, attributes, ct);

    public Task<(int BytesRead, bool Eof)> ReadAtAsync(byte[] fileHandle, ulong offset, byte[] buffer, int bufferOffset, int count, CancellationToken ct = default) =>
        RequireMounted().ReadAtAsync(fileHandle, offset, buffer, bufferOffset, count, ct);

    public Task<int> WriteAtAsync(byte[] fileHandle, ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        RequireMounted().WriteAtAsync(fileHandle, offset, data, ct);

    public Task<List<NfsEntry>> GetItemListAsync(string path, CancellationToken ct = default) =>
        RequireMounted().GetItemListAsync(path, ct);

    public Task<NfsFattr> GetItemAttributesAsync(string path, CancellationToken ct = default) =>
        RequireMounted().GetAttributesAsync(path, ct);

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default) =>
        RequireMounted().FileExistsAsync(path, ct);

    public Task<bool> IsDirectoryAsync(string path, CancellationToken ct = default) =>
        RequireMounted().IsDirectoryAsync(path, ct);

    public Task ReadAsync(string sourceRemotePath, Stream destination, CancellationToken ct = default) =>
        RequireMounted().ReadFileAsync(sourceRemotePath, destination, ct);

    public Task ReadAsync(string sourceRemotePath, string destinationLocalPath, CancellationToken ct = default) =>
        RequireMounted().ReadFileAsync(sourceRemotePath, destinationLocalPath, ct);

    public Task<NfsLookup> WriteAsync(string destinationRemotePath, Stream source, CancellationToken ct = default) =>
        RequireMounted().WriteFileAsync(destinationRemotePath, source, ct);

    public Task<NfsLookup> WriteAsync(string destinationRemotePath, string sourceLocalPath, CancellationToken ct = default) =>
        RequireMounted().WriteFileAsync(destinationRemotePath, sourceLocalPath, ct);

    public Task<NfsLookup> CreateFileAsync(string path, CancellationToken ct = default) =>
        RequireMounted().CreateFileAsync(path, ct);

    public Task<NfsLookup> CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        RequireMounted().CreateDirectoryAsync(path, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        RequireMounted().DeleteFileAsync(path, ct);

    public Task DeleteDirectoryAsync(string path, bool recursive = true, CancellationToken ct = default) =>
        RequireMounted().DeleteDirectoryAsync(path, recursive, ct);

    public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default) =>
        RequireMounted().MoveAsync(sourcePath, targetPath, ct);

    public Task<NfsLookup> CreateSymLinkAsync(string linkPath, string targetPath, CancellationToken ct = default) =>
        RequireMounted().CreateSymLinkAsync(linkPath, targetPath, ct);

    public Task CreateHardLinkAsync(string existingFilePath, string linkPath, CancellationToken ct = default) =>
        RequireMounted().CreateHardLinkAsync(existingFilePath, linkPath, ct);

    public Task<List<NfsEntryPlus>> ReadDirPlusAsync(string path, CancellationToken ct = default) =>
        RequireMounted().ReadDirPlusAsync(path, ct);

    public Task ChmodAsync(string path, uint mode, CancellationToken ct = default) =>
        RequireMounted().ChmodAsync(path, mode, ct);

    public Task ChownAsync(string path, uint uid, uint gid, CancellationToken ct = default) =>
        RequireMounted().ChownAsync(path, uid, gid, ct);

    public Task UtimesAsync(string path, DateTime? atime, DateTime? mtime, CancellationToken ct = default) =>
        RequireMounted().UtimesAsync(path, atime, mtime, ct);

    public Task SetFileSizeAsync(string path, ulong size, CancellationToken ct = default) =>
        RequireMounted().SetFileSizeAsync(path, size, ct);

    public Task<NfsAccessMode> AccessAsync(string path, NfsAccessMode desired, CancellationToken ct = default) =>
        RequireMounted().AccessAsync(path, desired, ct);

    public Task<string> ReadLinkAsync(string path, CancellationToken ct = default) =>
        RequireMounted().ReadLinkAsync(path, ct);

    public Task CommitAsync(string path, ulong offset, uint count, CancellationToken ct = default) =>
        RequireMounted().CommitAsync(path, offset, count, ct);

    public Task<NfsFileSystemStat> GetFileSystemStatAsync(string path = ".", CancellationToken ct = default) =>
        RequireMounted().GetFileSystemStatAsync(path, ct);

    public Task<NfsFileSystemInfo> GetFileSystemInfoAsync(string path = ".", CancellationToken ct = default) =>
        RequireMounted().GetFileSystemInfoAsync(path, ct);

    public Task<NfsPathConf> GetPathConfAsync(string path = ".", CancellationToken ct = default) =>
        RequireMounted().GetPathConfAsync(path, ct);

    public async ValueTask DisposeAsync()
    {
        if (_mounted is not null)
        {
            await _mounted.DisposeAsync();
            _mounted = null;
        }
    }

    private string RequireServer() =>
        string.IsNullOrWhiteSpace(_server)
            ? throw new NfsException("NFS server is not connected.")
            : _server;

    private NfsV3Client RequireMounted() =>
        _mounted ?? throw new NfsException("NFS export is not mounted.");

    private static void EnsureSupportedVersion(NfsVersion version)
    {
        if (version != NfsVersion.V3)
            throw new NotSupportedException("NfsSharp.Client currently implements NFSv3. NFSv2 and NFSv4.1 are not implemented.");
    }
}
