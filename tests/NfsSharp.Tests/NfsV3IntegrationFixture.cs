using System.Text;
using NfsSharp.Client;
using NfsSharp.Protocol;

namespace NfsSharp.Tests;

internal sealed class NfsV3IntegrationFixture : IAsyncDisposable
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public const string RootDirectory = "nfssharp-fixtures";
    public const string EmptyDirectory = $"{RootDirectory}/empty-dir";
    public const string NestedDirectory = $"{RootDirectory}/nested/child";
    public const string MutableRunsDirectory = $"{RootDirectory}/runs";
    public const string EmptyFilePath = $"{RootDirectory}/empty.txt";
    public const string SmallFilePath = $"{RootDirectory}/hello.txt";
    public const string NestedFilePath = $"{NestedDirectory}/data.bin";
    public const string UnicodeFilePath = $"{RootDirectory}/unicode-\u6d4b\u8bd5.txt";
    public const string SymlinkPath = $"{RootDirectory}/hello-link";
    public const string HardLinkPath = $"{RootDirectory}/hello-hardlink";
    public const string RestrictedDirectory = $"{RootDirectory}/no-access";
    public const string RestrictedFilePath = $"{RestrictedDirectory}/secret.txt";
    public const string SymlinkTarget = "hello.txt";

    public static readonly DateTime TimestampUtc =
        new(2024, 01, 02, 03, 04, 05, DateTimeKind.Utc);

    public static readonly string BoundaryFileName =
        "boundary-" + new string('x', 246);

    public static readonly string BoundaryFilePath =
        $"{RootDirectory}/{BoundaryFileName}";

    public static readonly NfsV3FixtureFile EmptyFile =
        new(EmptyFilePath, Array.Empty<byte>(), 0x1A4);

    public static readonly NfsV3FixtureFile SmallFile =
        new(SmallFilePath, Encoding.UTF8.GetBytes("NfsSharp NFSv3 fixture\r\nline 2\r\n"), 0x1A4);

    public static readonly NfsV3FixtureFile NestedFile =
        new(NestedFilePath, CreateBinaryContent(513), 0x180);

    public static readonly NfsV3FixtureFile UnicodeFile =
        new(UnicodeFilePath, Encoding.UTF8.GetBytes("unicode path fixture\r\n"), 0x1A4);

    public static readonly NfsV3FixtureFile BoundaryFile =
        new(BoundaryFilePath, CreateBinaryContent(4096), 0x1A4);

    public static readonly NfsV3FixtureFile RestrictedFile =
        new(RestrictedFilePath, Encoding.UTF8.GetBytes("permission fixture\r\n"), 0x180);

    private readonly NfsV3Client _client;
    private bool _disposed;

    private NfsV3IntegrationFixture(
        NfsV3Client client,
        string runDirectory,
        NfsV3FixtureCapabilities capabilities)
    {
        _client = client;
        RunDirectory = runDirectory;
        Capabilities = capabilities;
    }

    public string RunDirectory { get; }

    public NfsV3FixtureCapabilities Capabilities { get; }

    public static async Task<NfsV3IntegrationFixture> CreateAsync(NfsV3Client client, CancellationToken ct)
    {
        var capabilities = await EnsureSharedDataAsync(client, ct);
        var runDirectory = $"{MutableRunsDirectory}/run-{Guid.NewGuid():N}";
        await client.CreateDirectoryAsync(runDirectory, ct);
        return new NfsV3IntegrationFixture(client, runDirectory, capabilities);
    }

    public string GetRunPath(string name) => $"{RunDirectory}/{name}";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (await _client.FileExistsAsync(RunDirectory, timeout.Token))
                await _client.DeleteDirectoryAsync(RunDirectory, recursive: true, timeout.Token);
        }
        catch
        {
            // Best-effort cleanup. A unique run directory prevents cross-test leakage even if cleanup fails.
        }
    }

    private static async Task<NfsV3FixtureCapabilities> EnsureSharedDataAsync(
        NfsV3Client client,
        CancellationToken ct)
    {
        await SetupLock.WaitAsync(ct);
        try
        {
            await EnsureDirectoryAsync(client, RootDirectory, 0x1ED, ct);
            await EnsureDirectoryAsync(client, EmptyDirectory, 0x1ED, ct);
            await EnsureDirectoryAsync(client, "nfssharp-fixtures/nested", 0x1ED, ct);
            await EnsureDirectoryAsync(client, NestedDirectory, 0x1ED, ct);
            await EnsureDirectoryAsync(client, MutableRunsDirectory, 0x1ED, ct);

            await EnsureFileAsync(client, EmptyFile, ct);
            await EnsureFileAsync(client, SmallFile, ct);
            await EnsureFileAsync(client, NestedFile, ct);
            await EnsureFileAsync(client, UnicodeFile, ct);
            await EnsureFileAsync(client, BoundaryFile, ct);

            var supportsSymlinks = await EnsureSymbolicLinkAsync(client, ct);
            var supportsHardLinks = await EnsureHardLinkAsync(client, ct);
            var restrictedModeApplied = await EnsureRestrictedPermissionCaseAsync(client, ct);

            return new NfsV3FixtureCapabilities(
                supportsSymlinks,
                supportsHardLinks,
                restrictedModeApplied);
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static async Task EnsureDirectoryAsync(
        NfsV3Client client,
        string path,
        uint mode,
        CancellationToken ct)
    {
        if (!await client.FileExistsAsync(path, ct))
            await client.CreateDirectoryAsync(path, ct);

        var attributes = await client.GetAttributesAsync(path, ct);
        if (attributes.Type != NfsType.Dir)
            throw new InvalidOperationException($"Fixture path is not a directory: {path}");

        if ((attributes.Mode & 0x1FF) != mode)
            await client.ChmodAsync(path, mode, ct);
    }

    private static async Task EnsureFileAsync(
        NfsV3Client client,
        NfsV3FixtureFile file,
        CancellationToken ct)
    {
        var shouldWrite = true;
        if (await client.FileExistsAsync(file.Path, ct))
        {
            var attributes = await client.GetAttributesAsync(file.Path, ct);
            if (attributes.Type != NfsType.Reg)
                await DeleteExistingAsync(client, file.Path, attributes.Type, ct);
            else
                shouldWrite = !await HasContentAsync(client, file.Path, file.Content, ct);
        }

        if (shouldWrite)
        {
            await using var stream = new MemoryStream(file.Content, writable: false);
            await client.WriteFileAsync(file.Path, stream, ct);
        }

        var current = await client.GetAttributesAsync(file.Path, ct);
        if ((current.Mode & 0x1FF) != file.Mode)
            await client.ChmodAsync(file.Path, file.Mode, ct);

        await client.UtimesAsync(file.Path, TimestampUtc, TimestampUtc, ct);
    }

    private static async Task<bool> EnsureSymbolicLinkAsync(NfsV3Client client, CancellationToken ct)
    {
        if (await client.FileExistsAsync(SymlinkPath, ct))
        {
            try
            {
                if (await client.ReadLinkAsync(SymlinkPath, ct) == SymlinkTarget)
                    return true;
            }
            catch (NfsException)
            {
                // Recreate below when the existing path is not a usable symlink.
            }

            await DeleteExistingAsync(client, SymlinkPath, (await client.GetAttributesAsync(SymlinkPath, ct)).Type, ct);
        }

        try
        {
            await client.CreateSymLinkAsync(SymlinkPath, SymlinkTarget, ct);
            return true;
        }
        catch (NfsException ex) when (IsOptionalFixtureUnsupported(ex))
        {
            return false;
        }
    }

    private static async Task<bool> EnsureHardLinkAsync(NfsV3Client client, CancellationToken ct)
    {
        var source = await client.GetAttributesAsync(SmallFilePath, ct);
        if (await client.FileExistsAsync(HardLinkPath, ct))
        {
            var link = await client.GetAttributesAsync(HardLinkPath, ct);
            if (link.Type == NfsType.Reg && link.FileId == source.FileId)
                return true;

            await DeleteExistingAsync(client, HardLinkPath, link.Type, ct);
        }

        try
        {
            await client.CreateHardLinkAsync(SmallFilePath, HardLinkPath, ct);
            return true;
        }
        catch (NfsException ex) when (IsOptionalFixtureUnsupported(ex))
        {
            return false;
        }
    }

    private static async Task<bool> EnsureRestrictedPermissionCaseAsync(
        NfsV3Client client,
        CancellationToken ct)
    {
        await EnsureDirectoryAsync(client, RestrictedDirectory, 0x1C0, ct);
        await EnsureFileAsync(client, RestrictedFile, ct);

        try
        {
            await client.ChmodAsync(RestrictedDirectory, 0, ct);
            var attributes = await client.GetAttributesAsync(RestrictedDirectory, ct);
            return (attributes.Mode & 0x1FF) == 0;
        }
        catch (NfsException ex) when (IsOptionalFixtureUnsupported(ex))
        {
            return false;
        }
    }

    private static async Task<bool> HasContentAsync(
        NfsV3Client client,
        string path,
        byte[] expected,
        CancellationToken ct)
    {
        await using var output = new MemoryStream();
        await client.ReadFileAsync(path, output, ct);
        return output.ToArray().AsSpan().SequenceEqual(expected);
    }

    private static async Task DeleteExistingAsync(
        NfsV3Client client,
        string path,
        NfsType type,
        CancellationToken ct)
    {
        if (type == NfsType.Dir)
            await client.DeleteDirectoryAsync(path, recursive: true, ct);
        else
            await client.DeleteFileAsync(path, ct);
    }

    private static bool IsOptionalFixtureUnsupported(NfsException ex) =>
        ex.Status is NfsV3Status.NotSupp
            or NfsV3Status.Access
            or NfsV3Status.Perm
            or NfsV3Status.Inval;

    private static byte[] CreateBinaryContent(int length)
    {
        var content = new byte[length];
        for (var i = 0; i < content.Length; i++)
            content[i] = (byte)(i % 251);

        return content;
    }
}

internal sealed record NfsV3FixtureFile(string Path, byte[] Content, uint Mode)
{
    public long Size => Content.Length;
}

internal sealed record NfsV3FixtureCapabilities(
    bool SupportsSymbolicLinks,
    bool SupportsHardLinks,
    bool AppliesRestrictedModeBits);
