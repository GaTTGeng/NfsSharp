using NfsSharp.Client;
using NfsSharp.Protocol;

namespace NfsSharp.Tests;

public sealed class NfsV3IntegrationTests
{
    private const string MissingExportPath = "/missing-export";

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_ListsAdvertisedExportAndAccessGroups()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exports = await NfsV3Client.ListExportsAsync(
            NfsV3IntegrationEnvironment.Server,
            CreateOptions(),
            timeout.Token);

        var export = Assert.Single(
            exports,
            export => export.Path == NfsV3IntegrationEnvironment.ExportPath);

        if (NfsV3IntegrationEnvironment.ExpectedExportGroup is { } expectedGroup)
            Assert.Contains(expectedGroup, export.Groups);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_MountsAndUnmountsExportRepeatedly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = CreateOptions();

        for (var i = 0; i < 3; i++)
        {
            await using var client = await NfsV3Client.ConnectAsync(
                NfsV3IntegrationEnvironment.Server,
                NfsV3IntegrationEnvironment.ExportPath,
                options,
                timeout.Token);

            var attributes = await client.GetAttributesAsync(
                client.RootHandle,
                timeout.Token);

            Assert.Equal(NfsType.Dir, attributes.Type);
            Assert.NotEmpty(client.RootHandle);

            await client.UnmountAsync(timeout.Token);
            await client.UnmountAsync(timeout.Token);

            await Assert.ThrowsAsync<NfsException>(
                () => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        }
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_InvalidExportMountThrowsStableException()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ConnectAsync(
                NfsV3IntegrationEnvironment.Server,
                MissingExportPath,
                CreateOptions(),
                timeout.Token));

        Assert.NotNull(exception.Status);
        Assert.Contains($"MOUNT \"{MissingExportPath}\" failed", exception.Message);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_CreatesLooksUpAndEnumeratesDirectoryMetadata()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);
        var directory = fixture.GetRunPath(CreateUniquePath("metadata"));

        try
        {
            var created = await client.CreateDirectoryAsync(directory, timeout.Token);
            Assert.NotEmpty(created.Handle);

            var lookup = await client.LookupPathAsync(directory, timeout.Token);
            Assert.NotEmpty(lookup.Handle);

            var attributes = await client.GetAttributesAsync(directory, timeout.Token);
            Assert.Equal(NfsType.Dir, attributes.Type);
            Assert.True(attributes.Mode > 0);
            Assert.True(attributes.FileId > 0);

            Assert.True(await client.FileExistsAsync(directory, timeout.Token));
            Assert.True(await client.IsDirectoryAsync(directory, timeout.Token));
            Assert.False(await client.FileExistsAsync($"{directory}/missing", timeout.Token));

            var entries = await client.ReadDirAsync(".", timeout.Token);
            Assert.Contains(entries, entry => entry.Name == NfsV3IntegrationFixture.RootDirectory);

            var plusEntries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
            var plusEntry = Assert.Single(plusEntries, entry => entry.Name == Path.GetFileName(directory));
            Assert.Equal(NfsType.Dir, plusEntry.Attr?.Type);
            Assert.NotNull(plusEntry.Handle);
            Assert.NotEmpty(plusEntry.Handle);
        }
        finally
        {
            if (await client.FileExistsAsync(directory, timeout.Token))
                await client.DeleteDirectoryAsync(directory, recursive: true, timeout.Token);
        }

        Assert.False(await client.FileExistsAsync(directory, timeout.Token));
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_MaterializesDeterministicFixtureData()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        await AssertDirectoryAsync(client, NfsV3IntegrationFixture.RootDirectory, timeout.Token);
        await AssertDirectoryAsync(client, NfsV3IntegrationFixture.EmptyDirectory, timeout.Token);
        await AssertDirectoryAsync(client, NfsV3IntegrationFixture.NestedDirectory, timeout.Token);

        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.EmptyFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.SmallFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.NestedFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.UnicodeFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.BoundaryFile, timeout.Token);

        if (fixture.Capabilities.SupportsSymbolicLinks)
        {
            var target = await client.ReadLinkAsync(NfsV3IntegrationFixture.SymlinkPath, timeout.Token);
            Assert.Equal(NfsV3IntegrationFixture.SymlinkTarget, target);
        }

        if (fixture.Capabilities.SupportsHardLinks)
        {
            var source = await client.GetAttributesAsync(NfsV3IntegrationFixture.SmallFilePath, timeout.Token);
            var link = await client.GetAttributesAsync(NfsV3IntegrationFixture.HardLinkPath, timeout.Token);
            Assert.Equal(source.FileId, link.FileId);
            Assert.True(link.LinkCount >= 2);
        }

        if (fixture.Capabilities.AppliesRestrictedModeBits)
        {
            var restricted = await client.GetAttributesAsync(NfsV3IntegrationFixture.RestrictedDirectory, timeout.Token);
            Assert.Equal(0u, restricted.Mode & 0x1FF);
        }
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_ReadDirCoversEmptySmallAndNestedFixtureDirectories()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var emptyEntries = await client.ReadDirAsync(NfsV3IntegrationFixture.EmptyDirectory, timeout.Token);
        Assert.DoesNotContain(emptyEntries, entry => !IsSpecialDirectoryEntry(entry.Name));

        var rootEntries = await client.ReadDirAsync(NfsV3IntegrationFixture.RootDirectory, timeout.Token);
        AssertContainsEntry(rootEntries, "empty-dir");
        AssertContainsEntry(rootEntries, "nested");
        AssertContainsEntry(rootEntries, "hello.txt");
        AssertContainsEntry(rootEntries, Path.GetFileName(NfsV3IntegrationFixture.UnicodeFilePath));
        AssertNoDuplicateEntryNames(rootEntries);

        var nestedEntries = await client.ReadDirAsync(NfsV3IntegrationFixture.NestedDirectory, timeout.Token);
        var nestedFile = AssertContainsEntry(nestedEntries, "data.bin");
        var nestedAttributes = await client.GetAttributesAsync(NfsV3IntegrationFixture.NestedFilePath, timeout.Token);
        Assert.Equal((ulong)nestedAttributes.FileId, nestedFile.FileId);
        AssertNoDuplicateEntryNames(nestedEntries);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_ReadDirPlusReturnsAttributesAndHandlesForFixtureDirectory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var entries = await client.ReadDirPlusAsync(NfsV3IntegrationFixture.RootDirectory, timeout.Token);

        var fileEntry = AssertContainsEntry(entries, "hello.txt");
        Assert.NotNull(fileEntry.Attr);
        Assert.Equal(NfsType.Reg, fileEntry.Attr.Type);
        Assert.Equal((ulong)fileEntry.Attr.FileId, fileEntry.FileId);
        Assert.NotNull(fileEntry.Handle);
        Assert.NotEmpty(fileEntry.Handle);

        var handleAttributes = await client.GetAttributesAsync(fileEntry.Handle, timeout.Token);
        Assert.Equal(fileEntry.Attr.FileId, handleAttributes.FileId);
        Assert.Equal(fileEntry.Attr.Type, handleAttributes.Type);

        var directoryEntry = AssertContainsEntry(entries, "nested");
        Assert.NotNull(directoryEntry.Attr);
        Assert.Equal(NfsType.Dir, directoryEntry.Attr.Type);
        Assert.Equal((ulong)directoryEntry.Attr.FileId, directoryEntry.FileId);

        AssertNoDuplicateEntryNames(entries);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_ReadDirAndReadDirPlusCompleteCookiePaginationWithoutDuplicates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);

        var directory = fixture.GetRunPath("paged-directory");
        await setupClient.CreateDirectoryAsync(directory, timeout.Token);

        var expectedNames = Enumerable
            .Range(0, 40)
            .Select(i => $"entry-{i:00}.txt")
            .ToArray();

        foreach (var name in expectedNames)
        {
            await using var content = new MemoryStream([(byte)name.Length], writable: false);
            await setupClient.WriteFileAsync($"{directory}/{name}", content, timeout.Token);
        }

        await using var pagedClient = await ConnectV3ClientAsync(readdirCount: 1024, timeout.Token);

        var readDirEntries = await pagedClient.ReadDirAsync(directory, timeout.Token);
        AssertDirectoryEntries(readDirEntries.Select(entry => entry.Name), expectedNames);
        AssertNoDuplicateEntryNames(readDirEntries);

        var plusEntries = await pagedClient.ReadDirPlusAsync(directory, timeout.Token);
        AssertDirectoryEntries(plusEntries.Select(entry => entry.Name), expectedNames);
        AssertNoDuplicateEntryNames(plusEntries);

        foreach (var entry in plusEntries.Where(entry => expectedNames.Contains(entry.Name)))
        {
            Assert.NotNull(entry.Attr);
            Assert.Equal(NfsType.Reg, entry.Attr.Type);
            Assert.Equal(1, entry.Attr.Size);
            Assert.Equal((ulong)entry.Attr.FileId, entry.FileId);
            Assert.NotNull(entry.Handle);
            Assert.NotEmpty(entry.Handle);
        }
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_CanceledExportListThrowsOperationCanceled()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NfsV3Client.ListExportsAsync(
                NfsV3IntegrationEnvironment.Server,
                CreateOptions(),
                canceled.Token));
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_ListsMountsUnmountsAndRemountsExport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions());

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);

        var exports = await client.GetExportedDevicesAsync(timeout.Token);
        Assert.Contains(exports, export => export.Path == NfsV3IntegrationEnvironment.ExportPath);
        Assert.True(client.IsConnected);
        Assert.False(client.IsMounted);

        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);
        Assert.True(client.IsMounted);
        Assert.NotEmpty(client.RootHandle);

        await client.UnMountDeviceAsync(timeout.Token);
        Assert.False(client.IsMounted);
        await Assert.ThrowsAsync<NfsException>(
            () => client.GetItemAttributesAsync(".", timeout.Token));

        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);
        Assert.True(client.IsMounted);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_InvalidExportMountLeavesFacadeUnmounted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions());

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => client.MountDeviceAsync(MissingExportPath, timeout.Token));

        Assert.Contains($"MOUNT \"{MissingExportPath}\" failed", exception.Message);
        Assert.False(client.IsMounted);
        await Assert.ThrowsAsync<NfsException>(
            () => client.GetItemAttributesAsync(".", timeout.Token));
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_RemountReplacesActiveExportAndDisposeCleansUp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = new NfsClient(NfsVersion.V3, CreateOptions());

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);
        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);
        var firstRootHandle = client.RootHandle;

        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);
        Assert.True(client.IsMounted);
        Assert.NotEmpty(client.RootHandle);
        Assert.NotSame(firstRootHandle, client.RootHandle);

        await client.DisposeAsync();
        Assert.False(client.IsMounted);
        await Assert.ThrowsAsync<NfsException>(
            () => client.GetItemAttributesAsync(".", timeout.Token));
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_CanceledExportListThrowsOperationCanceled()
    {
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions());
        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetExportedDevicesAsync(canceled.Token));
    }

    private static NfsClientOptions CreateOptions(int? readdirCount = null) =>
        new()
        {
            UserId = NfsV3IntegrationEnvironment.UserId,
            GroupId = NfsV3IntegrationEnvironment.GroupId,
            UsePrivilegedSourcePort = false,
            CommandTimeout = TimeSpan.FromSeconds(10),
            MaxRetries = 0,
            ReaddirCount = readdirCount ?? NfsClientOptions.Default.ReaddirCount
        };

    private static Task<NfsV3Client> ConnectV3ClientAsync(CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            CreateOptions(),
            ct);

    private static Task<NfsV3Client> ConnectV3ClientAsync(int readdirCount, CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            CreateOptions(readdirCount),
            ct);

    private static string CreateUniquePath(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";

    private static async Task AssertDirectoryAsync(
        NfsV3Client client,
        string path,
        CancellationToken ct)
    {
        var attributes = await client.GetAttributesAsync(path, ct);
        Assert.Equal(NfsType.Dir, attributes.Type);
    }

    private static async Task AssertFixtureFileAsync(
        NfsV3Client client,
        NfsV3FixtureFile file,
        CancellationToken ct)
    {
        var attributes = await client.GetAttributesAsync(file.Path, ct);
        Assert.Equal(NfsType.Reg, attributes.Type);
        Assert.Equal(file.Size, attributes.Size);
        Assert.Equal(file.Mode, attributes.Mode & 0x1FF);

        await using var output = new MemoryStream();
        await client.ReadFileAsync(file.Path, output, ct);
        Assert.Equal(file.Content, output.ToArray());
    }

    private static NfsEntry AssertContainsEntry(IEnumerable<NfsEntry> entries, string name) =>
        Assert.Single(entries, entry => entry.Name == name);

    private static NfsEntryPlus AssertContainsEntry(IEnumerable<NfsEntryPlus> entries, string name) =>
        Assert.Single(entries, entry => entry.Name == name);

    private static void AssertDirectoryEntries(IEnumerable<string> actualNames, string[] expectedNames)
    {
        var actual = actualNames
            .Where(name => !IsSpecialDirectoryEntry(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedNames.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    private static void AssertNoDuplicateEntryNames(IEnumerable<NfsEntry> entries) =>
        AssertNoDuplicateEntryNames(entries.Select(entry => entry.Name));

    private static void AssertNoDuplicateEntryNames(IEnumerable<NfsEntryPlus> entries) =>
        AssertNoDuplicateEntryNames(entries.Select(entry => entry.Name));

    private static void AssertNoDuplicateEntryNames(IEnumerable<string> names)
    {
        var nonSpecialNames = names
            .Where(name => !IsSpecialDirectoryEntry(name))
            .ToArray();
        Assert.Equal(nonSpecialNames.Length, nonSpecialNames.Distinct(StringComparer.Ordinal).Count());
    }

    private static bool IsSpecialDirectoryEntry(string name) => name is "." or "..";

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task SmokeTest_ListsAndMountsExport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = CreateOptions();
        await using var client = await NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            options,
            timeout.Token);

        var attributes = await client.GetAttributesAsync(
            client.RootHandle,
            timeout.Token);

        Assert.Equal(NfsType.Dir, attributes.Type);
        Assert.NotEmpty(client.RootHandle);

        await client.UnmountAsync(timeout.Token);
    }
}
