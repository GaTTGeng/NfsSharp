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
    public async Task NfsV3Client_VerifiesLookupAttributesAccessAndExpectedFailures()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var rootLookup = await client.LookupPathAsync(".", timeout.Token);
        Assert.Equal(client.RootHandle, rootLookup.Handle);
        Assert.Equal(NfsType.Dir, rootLookup.Attr?.Type);

        var fixtureRoot = await client.LookupPathAsync(NfsV3IntegrationFixture.RootDirectory, timeout.Token);
        AssertLookupAttributes(fixtureRoot, NfsType.Dir);
        var fixtureRootAttributes = await client.GetAttributesAsync(fixtureRoot.Handle, timeout.Token);
        Assert.Equal(fixtureRoot.Attr!.FileId, fixtureRootAttributes.FileId);
        Assert.Equal(0x1EDu, fixtureRootAttributes.Mode & 0x1FF);

        var nestedDirectory = await client.GetAttributesAsync(
            NfsV3IntegrationFixture.NestedDirectory,
            timeout.Token);
        Assert.Equal(NfsType.Dir, nestedDirectory.Type);
        Assert.Equal(0x1EDu, nestedDirectory.Mode & 0x1FF);
        Assert.True(nestedDirectory.FileSystemId > 0);

        var fileLookup = await client.LookupPathAsync(NfsV3IntegrationFixture.SmallFilePath, timeout.Token);
        AssertLookupAttributes(fileLookup, NfsType.Reg);
        var fileByHandle = await client.GetAttributesAsync(fileLookup.Handle, timeout.Token);
        Assert.Equal(NfsV3IntegrationFixture.SmallFile.Size, fileByHandle.Size);
        Assert.Equal(NfsV3IntegrationFixture.SmallFile.Mode, fileByHandle.Mode & 0x1FF);
        Assert.Equal(fileLookup.Attr!.FileId, fileByHandle.FileId);
        AssertCloseTo(NfsV3IntegrationFixture.TimestampUtc, fileByHandle.Mtime);
        Assert.NotNull(fileByHandle.Atime);
        Assert.NotNull(fileByHandle.Ctime);

        var fileAccess = await client.AccessAsync(
            NfsV3IntegrationFixture.SmallFilePath,
            NfsAccessMode.Read | NfsAccessMode.Modify | NfsAccessMode.Extend,
            timeout.Token);
        AssertAccessGranted(fileAccess, NfsAccessMode.Read | NfsAccessMode.Modify | NfsAccessMode.Extend);

        var directoryAccess = await client.AccessAsync(
            fixtureRoot.Handle,
            NfsAccessMode.Read | NfsAccessMode.Lookup,
            timeout.Token);
        AssertAccessGranted(directoryAccess, NfsAccessMode.Read | NfsAccessMode.Lookup);

        var missing = await Assert.ThrowsAsync<NfsException>(
            () => client.LookupPathAsync($"{NfsV3IntegrationFixture.RootDirectory}/missing", timeout.Token));
        Assert.True(missing.IsNotFound);
        Assert.Equal(NfsV3Status.NoEnt, missing.Status);

        var notDirectory = await Assert.ThrowsAsync<NfsException>(
            () => client.LookupPathAsync($"{NfsV3IntegrationFixture.SmallFilePath}/child", timeout.Token));
        Assert.Equal(NfsV3Status.NotDir, notDirectory.Status);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesSymbolicLinkLookupAndTraversalBoundaries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var traversal = await Assert.ThrowsAsync<NfsException>(
            () => client.LookupPathAsync($"{NfsV3IntegrationFixture.RootDirectory}/../outside", timeout.Token));
        Assert.Null(traversal.Status);
        Assert.Contains("Parent path traversal", traversal.Message);

        if (!fixture.Capabilities.SupportsSymbolicLinks)
            return;

        var lookup = await client.LookupPathAsync(NfsV3IntegrationFixture.SymlinkPath, timeout.Token);
        AssertLookupAttributes(lookup, NfsType.Lnk);

        var targetByPath = await client.ReadLinkAsync(NfsV3IntegrationFixture.SymlinkPath, timeout.Token);
        var targetByHandle = await client.ReadLinkAsync(lookup.Handle, timeout.Token);
        Assert.Equal(NfsV3IntegrationFixture.SymlinkTarget, targetByPath);
        Assert.Equal(targetByPath, targetByHandle);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesRestrictedPathAccessBehavior()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);

        if (!fixture.Capabilities.AppliesRestrictedModeBits)
            return;

        await using var deniedClient = await ConnectV3ClientAsync(userId: 65534, groupId: 65534, timeout.Token);

        var granted = await deniedClient.AccessAsync(
            NfsV3IntegrationFixture.RestrictedDirectory,
            NfsAccessMode.Read | NfsAccessMode.Lookup,
            timeout.Token);
        Assert.Equal(NfsAccessMode.None, granted & (NfsAccessMode.Read | NfsAccessMode.Lookup));

        var denied = await Assert.ThrowsAsync<NfsException>(
            () => deniedClient.GetAttributesAsync(NfsV3IntegrationFixture.RestrictedFilePath, timeout.Token));
        Assert.Contains(denied.Status, new uint?[] { NfsV3Status.Access, NfsV3Status.Perm });
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
    public async Task NfsV3Client_ReadAtReportsCountsAndEofForFixtureOffsets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.EmptyFile,
            offset: 0,
            count: 8,
            expectedEof: true,
            ct: timeout.Token);

        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.SmallFile,
            offset: 5,
            count: 7,
            expectedEof: false,
            ct: timeout.Token);

        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.SmallFile,
            offset: (ulong)NfsV3IntegrationFixture.SmallFile.Size - 1,
            count: 16,
            expectedEof: true,
            ct: timeout.Token);

        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.BoundaryFile,
            offset: (ulong)NfsV3IntegrationFixture.BoundaryFile.Size - 7,
            count: 32,
            expectedEof: true,
            ct: timeout.Token);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_ReadFileStreamsExactBytesWithConfiguredChunkSize()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);

        var fsInfo = await setupClient.GetFileSystemInfoAsync(
            NfsV3IntegrationFixture.BoundaryFile.Path,
            timeout.Token);
        Assert.True(fsInfo.MaxReadSize > 0);
        Assert.True(fsInfo.PreferredReadSize > 0);

        var configuredReadSize = (int)Math.Min(257u, fsInfo.MaxReadSize);
        Assert.True(configuredReadSize > 0);
        Assert.True(NfsV3IntegrationFixture.BoundaryFile.Content.Length > configuredReadSize);

        await using var chunkedClient = await ConnectV3ClientAsync(
            CreateOptions(maxReadSize: configuredReadSize),
            timeout.Token);

        await using var output = new MemoryStream();
        await chunkedClient.ReadFileAsync(NfsV3IntegrationFixture.BoundaryFile.Path, output, timeout.Token);

        Assert.Equal(NfsV3IntegrationFixture.BoundaryFile.Content, output.ToArray());
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_ReadFailuresCoverCancellationInvalidHandleAndMissingPath()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var lookup = await client.LookupPathAsync(NfsV3IntegrationFixture.SmallFile.Path, timeout.Token);
        var buffer = new byte[16];

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReadAtAsync(lookup.Handle, 0, buffer, 0, buffer.Length, canceled.Token));

        var invalidHandle = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadAtAsync(Array.Empty<byte>(), 0, buffer, 0, buffer.Length, timeout.Token));
        Assert.Contains("file handle is empty", invalidHandle.Message);

        await using var output = new MemoryStream();
        var missingPath = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadFileAsync(fixture.GetRunPath("missing-read-source.bin"), output, timeout.Token));
        Assert.Equal(NfsV3Status.NoEnt, missingPath.Status);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesFileSystemStatInfoAndPathConfByHandleAndPath()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var lookup = await client.LookupPathAsync(NfsV3IntegrationFixture.BoundaryFile.Path, timeout.Token);

        var statByPath = await client.GetFileSystemStatAsync(NfsV3IntegrationFixture.BoundaryFile.Path, timeout.Token);
        var statByHandle = await client.GetFileSystemStatAsync(lookup.Handle, timeout.Token);
        AssertFileSystemStat(statByPath);
        AssertFileSystemStat(statByHandle);

        var infoByPath = await client.GetFileSystemInfoAsync(NfsV3IntegrationFixture.BoundaryFile.Path, timeout.Token);
        var infoByHandle = await client.GetFileSystemInfoAsync(lookup.Handle, timeout.Token);
        Assert.Equal(infoByPath, infoByHandle);
        AssertFileSystemInfo(infoByPath, fixture.Capabilities);

        var pathConfByPath = await client.GetPathConfAsync(NfsV3IntegrationFixture.BoundaryFile.Path, timeout.Token);
        var pathConfByHandle = await client.GetPathConfAsync(lookup.Handle, timeout.Token);
        Assert.Equal(pathConfByPath, pathConfByHandle);
        AssertPathConf(pathConfByPath, fixture.Capabilities);

        await AssertPathConfCaseBehaviorAsync(client, fixture, pathConfByPath, timeout.Token);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_ReportsFileSystemCapabilitiesThroughFacade()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions());

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);
        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);

        var stat = await client.GetFileSystemStatAsync(NfsV3IntegrationFixture.RootDirectory, timeout.Token);
        var info = await client.GetFileSystemInfoAsync(NfsV3IntegrationFixture.RootDirectory, timeout.Token);
        var pathConf = await client.GetPathConfAsync(NfsV3IntegrationFixture.RootDirectory, timeout.Token);

        AssertFileSystemStat(stat);
        AssertFileSystemInfo(info, fixture.Capabilities);
        AssertPathConf(pathConf, fixture.Capabilities);
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

    private static NfsClientOptions CreateOptions(
        int? readdirCount = null,
        int? maxReadSize = null,
        uint? userId = null,
        uint? groupId = null) =>
        new()
        {
            UserId = userId ?? NfsV3IntegrationEnvironment.UserId,
            GroupId = groupId ?? NfsV3IntegrationEnvironment.GroupId,
            UsePrivilegedSourcePort = false,
            CommandTimeout = TimeSpan.FromSeconds(10),
            MaxRetries = 0,
            MaxReadSize = maxReadSize ?? NfsClientOptions.Default.MaxReadSize,
            ReaddirCount = readdirCount ?? NfsClientOptions.Default.ReaddirCount
        };

    private static Task<NfsV3Client> ConnectV3ClientAsync(CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            CreateOptions(),
            ct);

    private static Task<NfsV3Client> ConnectV3ClientAsync(NfsClientOptions options, CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            options,
            ct);

    private static Task<NfsV3Client> ConnectV3ClientAsync(int readdirCount, CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            CreateOptions(readdirCount),
            ct);

    private static Task<NfsV3Client> ConnectV3ClientAsync(uint userId, uint groupId, CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            CreateOptions(userId: userId, groupId: groupId),
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

    private static async Task AssertReadAtAsync(
        NfsV3Client client,
        NfsV3FixtureFile file,
        ulong offset,
        int count,
        bool expectedEof,
        CancellationToken ct)
    {
        var lookup = await client.LookupPathAsync(file.Path, ct);
        var buffer = Enumerable.Repeat((byte)0xCC, count + 4).ToArray();

        var (bytesRead, eof) = await client.ReadAtAsync(
            lookup.Handle,
            offset,
            buffer,
            bufferOffset: 2,
            count,
            ct);

        var available = Math.Max(0, file.Content.Length - (int)offset);
        var expected = file.Content
            .AsSpan((int)offset, Math.Min(count, available))
            .ToArray();

        Assert.Equal(expected.Length, bytesRead);
        Assert.Equal(expectedEof, eof);
        Assert.Equal(0xCC, buffer[0]);
        Assert.Equal(0xCC, buffer[1]);
        Assert.Equal(expected, buffer.AsSpan(2, bytesRead).ToArray());
        Assert.All(buffer.Skip(2 + bytesRead), value => Assert.Equal(0xCC, value));
    }

    private static NfsEntry AssertContainsEntry(IEnumerable<NfsEntry> entries, string name) =>
        Assert.Single(entries, entry => entry.Name == name);

    private static NfsEntryPlus AssertContainsEntry(IEnumerable<NfsEntryPlus> entries, string name) =>
        Assert.Single(entries, entry => entry.Name == name);

    private static void AssertLookupAttributes(NfsLookup lookup, NfsType type)
    {
        Assert.NotEmpty(lookup.Handle);
        Assert.NotNull(lookup.Attr);
        Assert.Equal(type, lookup.Attr.Type);
        Assert.True(lookup.Attr.FileId > 0);
    }

    private static void AssertAccessGranted(NfsAccessMode actual, NfsAccessMode expected) =>
        Assert.Equal(expected, actual & expected);

    private static void AssertCloseTo(DateTime expectedUtc, DateTime? actualUtc)
    {
        Assert.NotNull(actualUtc);
        var delta = (actualUtc.Value.ToUniversalTime() - expectedUtc).Duration();
        Assert.True(delta <= TimeSpan.FromSeconds(2), $"Expected {actualUtc:o} to be within 2 seconds of {expectedUtc:o}.");
    }

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

    private static void AssertFileSystemStat(NfsFileSystemStat stat)
    {
        if (stat.TotalBytes > 0)
        {
            Assert.True(stat.FreeBytes <= stat.TotalBytes);
            Assert.True(stat.AvailableBytes <= stat.FreeBytes);
        }
        else
        {
            Assert.Equal(0ul, stat.FreeBytes);
            Assert.Equal(0ul, stat.AvailableBytes);
        }

        if (stat.TotalFiles > 0)
        {
            Assert.True(stat.FreeFiles <= stat.TotalFiles);
            Assert.True(stat.AvailableFiles <= stat.FreeFiles);
        }
        else
        {
            Assert.Equal(0ul, stat.FreeFiles);
            Assert.Equal(0ul, stat.AvailableFiles);
        }

        Assert.True(stat.InvariantUntil >= TimeSpan.Zero);
    }

    private static void AssertFileSystemInfo(
        NfsFileSystemInfo info,
        NfsV3FixtureCapabilities capabilities)
    {
        const uint FsF3Link = 0x0001;
        const uint FsF3Symlink = 0x0002;
        const uint FsF3Homogeneous = 0x0008;
        const uint FsF3CanSetTime = 0x0010;
        const uint KnownFsInfoProperties = FsF3Link | FsF3Symlink | FsF3Homogeneous | FsF3CanSetTime;

        Assert.True(info.MaxReadSize > 0);
        Assert.True(info.PreferredReadSize > 0);
        Assert.True(info.PreferredReadSize <= info.MaxReadSize);
        Assert.True(info.ReadMultipleSize > 0);
        Assert.True(info.ReadMultipleSize <= info.MaxReadSize);

        Assert.True(info.MaxWriteSize > 0);
        Assert.True(info.PreferredWriteSize > 0);
        Assert.True(info.PreferredWriteSize <= info.MaxWriteSize);
        Assert.True(info.WriteMultipleSize > 0);
        Assert.True(info.WriteMultipleSize <= info.MaxWriteSize);

        Assert.True(info.PreferredReaddirSize > 0);
        Assert.True(info.MaxFileSize >= (ulong)NfsV3IntegrationFixture.BoundaryFile.Size);
        Assert.True(info.TimeDelta >= TimeSpan.Zero);
        Assert.Equal(0u, info.Properties & ~KnownFsInfoProperties);

        if (capabilities.SupportsHardLinks)
            Assert.NotEqual(0u, info.Properties & FsF3Link);

        if (capabilities.SupportsSymbolicLinks)
            Assert.NotEqual(0u, info.Properties & FsF3Symlink);

        Assert.NotEqual(0u, info.Properties & FsF3CanSetTime);
    }

    private static void AssertPathConf(
        NfsPathConf pathConf,
        NfsV3FixtureCapabilities capabilities)
    {
        if (pathConf.LinkMax > 0 && capabilities.SupportsHardLinks)
            Assert.True(pathConf.LinkMax >= 2);

        Assert.True(pathConf.NameMax >= NfsV3IntegrationFixture.BoundaryFileName.Length);
        Assert.True(pathConf.CaseInsensitive || pathConf.CasePreserving);
    }

    private static async Task AssertPathConfCaseBehaviorAsync(
        NfsV3Client client,
        NfsV3IntegrationFixture fixture,
        NfsPathConf pathConf,
        CancellationToken ct)
    {
        var name = "PathConf-MixedCase.txt";
        var path = fixture.GetRunPath(name);
        await using (var content = new MemoryStream([0x43], writable: false))
        {
            await client.WriteFileAsync(path, content, ct);
        }

        var entries = await client.ReadDirAsync(fixture.RunDirectory, ct);
        if (pathConf.CasePreserving)
            Assert.Contains(entries, entry => entry.Name == name);

        var alternateCasePath = fixture.GetRunPath(name.ToLowerInvariant());
        if (pathConf.CaseInsensitive)
        {
            var original = await client.GetAttributesAsync(path, ct);
            var alternate = await client.GetAttributesAsync(alternateCasePath, ct);
            Assert.Equal(original.FileId, alternate.FileId);
        }
        else
        {
            var missing = await Assert.ThrowsAsync<NfsException>(
                () => client.LookupPathAsync(alternateCasePath, ct));
            Assert.Equal(NfsV3Status.NoEnt, missing.Status);
        }
    }

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
