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

        var noAccessRequested = await client.AccessAsync(
            fileLookup.Handle,
            NfsAccessMode.None,
            timeout.Token);
        Assert.Equal(NfsAccessMode.None, noAccessRequested);

        var invalidAccess = await Assert.ThrowsAsync<NfsException>(
            () => client.AccessAsync(
                fileLookup.Handle,
                (NfsAccessMode)0x8000,
                timeout.Token));
        Assert.Contains("Invalid ACCESS mask", invalidAccess.Message);

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
    public async Task NfsV3Client_VerifiesCommonFailureStatusSemantics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var existingFile = fixture.GetRunPath("existing-file.txt");
        var existingDirectory = fixture.GetRunPath("existing-dir");
        var nonEmptyDirectory = fixture.GetRunPath("non-empty-dir");
        var staleFile = fixture.GetRunPath("stale-file.txt");

        await WriteBytesAsync(client, existingFile, [0x01], timeout.Token);
        await client.CreateDirectoryAsync(existingDirectory, timeout.Token);
        await client.CreateDirectoryAsync(nonEmptyDirectory, timeout.Token);
        await WriteBytesAsync(client, $"{nonEmptyDirectory}/child.txt", [0x02], timeout.Token);

        await AssertNfsStatusAsync(
            NfsV3Status.NoEnt,
            isNotFound: true,
            "LOOKUP",
            () => client.LookupPathAsync(fixture.GetRunPath("missing.txt"), timeout.Token));

        await AssertNfsStatusAsync(
            NfsV3Status.Exist,
            isNotFound: false,
            "CREATE",
            () => client.CreateFileAsync(existingFile, timeout.Token));

        await AssertNfsStatusAsync(
            NfsV3Status.Exist,
            isNotFound: false,
            "MKDIR",
            () => client.CreateDirectoryAsync(existingDirectory, timeout.Token));

        await AssertNfsStatusAsync(
            NfsV3Status.NotDir,
            isNotFound: false,
            "LOOKUP",
            () => client.LookupPathAsync($"{existingFile}/child", timeout.Token));

        await AssertNfsStatusAsync(
            NfsV3Status.IsDir,
            isNotFound: false,
            "REMOVE",
            () => client.DeleteFileAsync(existingDirectory, timeout.Token));

        await AssertNfsStatusAsync(
            NfsV3Status.NotDir,
            isNotFound: false,
            "RMDIR",
            () => client.DeleteDirectoryAsync(existingFile, recursive: false, timeout.Token));

        await AssertNfsStatusAsync(
            NfsV3Status.NotEmpty,
            isNotFound: false,
            "RMDIR",
            () => client.DeleteDirectoryAsync(nonEmptyDirectory, recursive: false, timeout.Token));

        var created = await client.CreateFileAsync(staleFile, timeout.Token);
        await client.DeleteFileAsync(staleFile, timeout.Token);
        await AssertNfsStatusAsync(
            NfsV3Status.Stale,
            isNotFound: false,
            "GETATTR",
            () => client.GetAttributesAsync(created.Handle, timeout.Token));
        await AssertNfsStatusAsync(
            NfsV3Status.Stale,
            isNotFound: false,
            "ACCESS",
            () => client.AccessAsync(created.Handle, NfsAccessMode.None, timeout.Token));

        var tooLongName = new string('x', 256);
        var tooLong = await Assert.ThrowsAsync<NfsException>(
            () => client.CreateFileAsync($"{fixture.RunDirectory}/{tooLongName}", timeout.Token));
        Assert.Null(tooLong.Status);
        Assert.False(tooLong.IsNotFound);
        Assert.Contains("too long", tooLong.Message);

        if (!fixture.Capabilities.AppliesRestrictedModeBits)
            return;

        await using var deniedClient = await ConnectV3ClientAsync(userId: 65534, groupId: 65534, timeout.Token);
        var denied = await Assert.ThrowsAsync<NfsException>(
            () => deniedClient.GetAttributesAsync(NfsV3IntegrationFixture.RestrictedFilePath, timeout.Token));

        Assert.Contains(denied.Status, new uint?[] { NfsV3Status.Access, NfsV3Status.Perm });
        Assert.False(denied.IsNotFound);
        Assert.NotNull(denied.Status);
        Assert.Contains(NfsV3Status.Describe(denied.Status.Value), denied.Message);
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
    public async Task NfsV3Client_DirectoryCacheKeepsReadDirPlusCoherentAfterSameClientMutations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(
            CreateOptions(
                enableDirectoryCache: true,
                directoryCacheTtl: TimeSpan.FromMinutes(5)),
            timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var path = fixture.GetRunPath("cache-coherence.txt");
        await WriteBytesAsync(client, path, [0x01], timeout.Token);

        var entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.Equal(1L, AssertContainsEntry(entries, "cache-coherence.txt").Attr?.Size);

        await client.SetFileSizeAsync(path, 4, timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.Equal(4L, AssertContainsEntry(entries, "cache-coherence.txt").Attr?.Size);

        await WriteBytesAsync(client, path, [0x02, 0x03], timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        var cachedEntry = AssertContainsEntry(entries, "cache-coherence.txt");
        Assert.Equal(2L, cachedEntry.Attr?.Size);
        if (cachedEntry.Handle is { Length: > 0 })
            cachedEntry.Handle[0] ^= 0xFF;
        entries.Clear();

        await client.SetFileSizeAsync(path, 5, timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.Equal(5L, AssertContainsEntry(entries, "cache-coherence.txt").Attr?.Size);

        var createdPath = fixture.GetRunPath("cache-created.txt");
        await client.CreateFileAsync(createdPath, timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        AssertContainsEntry(entries, "cache-created.txt");

        var renamedPath = fixture.GetRunPath("cache-renamed.txt");
        await client.MoveAsync(createdPath, renamedPath, timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.DoesNotContain(entries, entry => entry.Name == "cache-created.txt");
        AssertContainsEntry(entries, "cache-renamed.txt");

        await client.DeleteFileAsync(renamedPath, timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.DoesNotContain(entries, entry => entry.Name == "cache-renamed.txt");
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_DirectoryCacheExpiresForCrossClientMutations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var cachedClient = await ConnectV3ClientAsync(
            CreateOptions(
                enableDirectoryCache: true,
                directoryCacheTtl: TimeSpan.FromMilliseconds(500)),
            timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(cachedClient, timeout.Token);
        await using var writerClient = await ConnectV3ClientAsync(timeout.Token);

        var externalName = "cache-external.txt";
        var externalPath = fixture.GetRunPath(externalName);

        var entries = await cachedClient.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.DoesNotContain(entries, entry => entry.Name == externalName);

        await writerClient.CreateFileAsync(externalPath, timeout.Token);

        entries = await cachedClient.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.DoesNotContain(entries, entry => entry.Name == externalName);

        await Task.Delay(TimeSpan.FromMilliseconds(700), timeout.Token);

        entries = await cachedClient.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        AssertContainsEntry(entries, externalName);
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

        var negativeOffset = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadAtAsync(lookup.Handle, 0, buffer, -1, buffer.Length, timeout.Token));
        Assert.Contains("Buffer offset", negativeOffset.Message);

        var tooLargeRange = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadAtAsync(lookup.Handle, 0, buffer, 8, buffer.Length, timeout.Token));
        Assert.Contains("exceed the buffer length", tooLargeRange.Message);

        var zeroLengthRead = await client.ReadAtAsync(lookup.Handle, 0, buffer, 0, 0, timeout.Token);
        Assert.Equal(0, zeroLengthRead.BytesRead);
        Assert.False(zeroLengthRead.Eof);

        await using var limitedClient = await ConnectV3ClientAsync(
            CreateOptions(maxReadSize: 4),
            timeout.Token);
        var tooLargeRead = await Assert.ThrowsAsync<NfsException>(
            () => limitedClient.ReadAtAsync(lookup.Handle, 0, buffer, 0, 5, timeout.Token));
        Assert.Contains("exceeds MaxReadSize", tooLargeRead.Message);

        await using var output = new MemoryStream();
        var missingPath = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadFileAsync(fixture.GetRunPath("missing-read-source.bin"), output, timeout.Token));
        Assert.Equal(NfsV3Status.NoEnt, missingPath.Status);

        var localFailureDirectory = Path.Combine(Path.GetTempPath(), $"nfssharp-read-failure-{Guid.NewGuid():N}");
        var localFailurePath = Path.Combine(localFailureDirectory, "missing.bin");
        try
        {
            var missingLocalPath = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadFileAsync(fixture.GetRunPath("missing-read-source-local.bin"), localFailurePath, timeout.Token));
            Assert.Equal(NfsV3Status.NoEnt, missingLocalPath.Status);
            Assert.False(File.Exists(localFailurePath));
            Assert.False(Directory.Exists(localFailureDirectory));
        }
        finally
        {
            if (Directory.Exists(localFailureDirectory))
                Directory.Delete(localFailureDirectory, recursive: true);
        }

        await using var readOnlyOutput = new MemoryStream(new byte[8], writable: false);
        var notWritable = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadFileAsync(lookup.Handle, readOnlyOutput, timeout.Token));
        Assert.Contains("writable", notWritable.Message);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_WriteAtWriteFileAndCommitPersistExpectedBytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var offsetPath = fixture.GetRunPath("write-at.bin");
        var created = await client.CreateFileAsync(offsetPath, timeout.Token);

        var firstWrite = await client.WriteAtWithResultAsync(
            created.Handle,
            0,
            new byte[] { 0x10, 0x11, 0x12, 0x13 },
            timeout.Token);
        Assert.Equal(4, firstWrite.Count);
        AssertWriteResult(firstWrite);

        var overwrite = await client.WriteAtAsync(
            created.Handle,
            2,
            new byte[] { 0x20, 0x21, 0x22, 0x23 },
            timeout.Token);
        Assert.Equal(4, overwrite);

        var commit = await client.CommitWithResultAsync(created.Handle, 0, 0, timeout.Token);
        AssertCommitResult(commit);
        commit = await client.CommitWithResultAsync(created.Handle, 2, 3, timeout.Token);
        AssertCommitResult(commit);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x20, 0x21, 0x22, 0x23 }, await ReadBytesAsync(client, offsetPath, timeout.Token));

        var streamPath = fixture.GetRunPath("stream-write.bin");
        var streamContent = Enumerable.Range(0, 19).Select(i => (byte)(0x40 + i)).ToArray();
        NfsLookup streamLookup;
        await using (var input = new MemoryStream(streamContent, writable: false))
        {
            streamLookup = await client.WriteFileAsync(streamPath, input, timeout.Token);
        }

        Assert.NotNull(streamLookup.Attr);
        Assert.Equal(streamContent.Length, streamLookup.Attr.Size);
        commit = await client.CommitWithResultAsync(streamPath, 0, (uint)streamContent.Length, timeout.Token);
        AssertCommitResult(commit);
        commit = await client.CommitWithResultAsync(streamPath, 4, 7, timeout.Token);
        AssertCommitResult(commit);
        Assert.Equal(streamContent, await ReadBytesAsync(client, streamPath, timeout.Token));

        var replacementContent = new byte[] { 0x55, 0x56, 0x57 };
        await using (var input = new MemoryStream(replacementContent, writable: false))
        {
            streamLookup = await client.WriteFileAsync(streamPath, input, timeout.Token);
        }

        Assert.NotNull(streamLookup.Attr);
        Assert.Equal(replacementContent.Length, streamLookup.Attr.Size);
        Assert.Equal(replacementContent, await ReadBytesAsync(client, streamPath, timeout.Token));

        await using var chunkedClient = await ConnectV3ClientAsync(
            CreateOptions(maxWriteSize: 3),
            timeout.Token);

        var chunkedPath = fixture.GetRunPath("chunked-stream-write.bin");
        var chunkedContent = Enumerable.Range(0, 17).Select(i => (byte)(0x70 + i)).ToArray();
        NfsLookup chunkedLookup;
        await using (var input = new MemoryStream(chunkedContent, writable: false))
        {
            chunkedLookup = await chunkedClient.WriteFileAsync(chunkedPath, input, timeout.Token);
        }

        Assert.NotNull(chunkedLookup.Attr);
        Assert.Equal(chunkedContent.Length, chunkedLookup.Attr.Size);
        await chunkedClient.CommitAsync(chunkedPath, 0, 0, timeout.Token);
        Assert.Equal(chunkedContent, await ReadBytesAsync(client, chunkedPath, timeout.Token));

        var zeroLengthWrite = await client.WriteAtAsync(created.Handle, 0, ReadOnlyMemory<byte>.Empty, timeout.Token);
        Assert.Equal(0, zeroLengthWrite);
        var zeroLengthWriteResult = await client.WriteAtWithResultAsync(created.Handle, 0, ReadOnlyMemory<byte>.Empty, timeout.Token);
        Assert.Equal(0, zeroLengthWriteResult.Count);
        Assert.Empty(zeroLengthWriteResult.WriteVerifier);

        await using var limitedClient = await ConnectV3ClientAsync(
            CreateOptions(maxWriteSize: 2),
            timeout.Token);
        var tooLargeWrite = await Assert.ThrowsAsync<NfsException>(
            () => limitedClient.WriteAtAsync(created.Handle, 0, new byte[] { 0x01, 0x02, 0x03 }, timeout.Token));
        Assert.Contains("exceeds MaxWriteSize", tooLargeWrite.Message);

        var rejectedPath = fixture.GetRunPath("non-readable-stream.bin");
        await using var nonReadableInput = new NonReadableStream();
        var notReadable = await Assert.ThrowsAsync<NfsException>(
            () => client.WriteFileAsync(rejectedPath, nonReadableInput, timeout.Token));
        Assert.Contains("readable", notReadable.Message);
        await AssertMissingPathAsync(client, rejectedPath, timeout.Token);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_CanceledWritesDoNotReportSuccessOrCreateFiles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var existingPath = fixture.GetRunPath("canceled-write-existing.bin");
        var created = await client.CreateFileAsync(existingPath, timeout.Token);
        var originalContent = new byte[] { 0x10, 0x11 };
        await client.WriteAtAsync(created.Handle, 0, originalContent, timeout.Token);

        var newPath = fixture.GetRunPath("canceled-write-new.bin");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WriteAtAsync(created.Handle, 0, new byte[] { 0x20 }, canceled.Token));
        Assert.Equal(originalContent, await ReadBytesAsync(client, existingPath, timeout.Token));

        await using var input = new MemoryStream(new byte[] { 0x30 }, writable: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WriteFileAsync(newPath, input, canceled.Token));
        await AssertMissingPathAsync(client, newPath, timeout.Token);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_WritesAndCommitsThroughFacade()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions(maxWriteSize: 2));

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);
        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);

        var path = fixture.GetRunPath("facade-write.bin");
        var created = await client.CreateAndOpenFileAsync(path, null, timeout.Token);
        var written = await client.WriteAtAsync(created.Handle, 0, new byte[] { 0x01, 0x02 }, timeout.Token);
        Assert.Equal(2, written);

        written = await client.WriteAtAsync(created.Handle, 4, new byte[] { 0x05, 0x06 }, timeout.Token);
        Assert.Equal(2, written);

        var commit = await client.CommitWithResultAsync(path, 0, 0, timeout.Token);
        AssertCommitResult(commit);

        await using var sparseOutput = new MemoryStream();
        await client.ReadAsync(path, sparseOutput, timeout.Token);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x00, 0x05, 0x06 }, sparseOutput.ToArray());

        var replacement = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        await using (var input = new MemoryStream(replacement, writable: false))
        {
            await client.WriteAsync(path, input, timeout.Token);
        }

        commit = await client.CommitWithResultAsync(path, 0, (uint)replacement.Length, timeout.Token);
        AssertCommitResult(commit);

        await using var output = new MemoryStream();
        await client.ReadAsync(path, output, timeout.Token);
        Assert.Equal(replacement, output.ToArray());
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_WriteAtWithResultReportsCommittedStabilityForConfiguredModes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);

        foreach (var requested in Enum.GetValues<NfsWriteStableHow>())
        {
            await using var client = await ConnectV3ClientAsync(
                CreateOptions(stableHow: requested),
                timeout.Token);

            var path = fixture.GetRunPath($"stable-{requested}.bin");
            var created = await client.CreateFileAsync(path, timeout.Token);
            var content = new byte[] { 0x30, 0x31, (byte)requested };

            var write = await client.WriteAtWithResultAsync(created.Handle, 0, content, timeout.Token);

            Assert.Equal(content.Length, write.Count);
            AssertWriteResult(write);
            AssertCommittedAtLeast(requested, write.Committed);

            var commit = await client.CommitWithResultAsync(created.Handle, 0, (uint)content.Length, timeout.Token);
            AssertCommitResult(commit);
            Assert.Equal(content, await ReadBytesAsync(setupClient, path, timeout.Token));
        }
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesFileAndDirectoryCreateAndRemoveBehavior()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var parent = await client.LookupPathAsync(fixture.RunDirectory, timeout.Token);
        var directory = fixture.GetRunPath("created-dir");
        var file = $"{directory}/created-file.txt";
        var emptyDirectory = fixture.GetRunPath("empty-delete");
        var nonEmptyDirectory = fixture.GetRunPath("non-empty-delete");
        var nestedFile = $"{nonEmptyDirectory}/child.txt";

        var createdDirectory = await client.CreateDirectoryAsync(
            parent.Handle,
            "created-dir",
            new NfsSetAttributes { Mode = 0x1C0 },
            timeout.Token);
        AssertLookupAttributes(createdDirectory, NfsType.Dir);

        var directoryAttributes = await client.GetAttributesAsync(directory, timeout.Token);
        Assert.Equal(NfsType.Dir, directoryAttributes.Type);
        Assert.Equal(0x1C0u, directoryAttributes.Mode & 0x1FF);

        var directoryLookup = await client.LookupPathAsync(directory, timeout.Token);
        var createdFile = await client.CreateFileAsync(
            directoryLookup.Handle,
            "created-file.txt",
            new NfsSetAttributes { Mode = 0x180 },
            timeout.Token);
        AssertLookupAttributes(createdFile, NfsType.Reg);

        var fileAttributes = await client.GetAttributesAsync(file, timeout.Token);
        Assert.Equal(NfsType.Reg, fileAttributes.Type);
        Assert.Equal(0x180u, fileAttributes.Mode & 0x1FF);

        await client.DeleteFileAsync(file, timeout.Token);
        await AssertMissingPathAsync(client, file, timeout.Token);
        var deletedHandle = await Assert.ThrowsAsync<NfsException>(
            () => client.GetAttributesAsync(createdFile.Handle, timeout.Token));
        Assert.Equal(NfsV3Status.Stale, deletedHandle.Status);

        await client.CreateDirectoryAsync(emptyDirectory, timeout.Token);
        await client.DeleteDirectoryAsync(emptyDirectory, recursive: false, timeout.Token);
        await AssertMissingPathAsync(client, emptyDirectory, timeout.Token);

        await client.CreateDirectoryAsync(nonEmptyDirectory, timeout.Token);
        await WriteBytesAsync(client, nestedFile, [0x4E], timeout.Token);

        var notEmpty = await Assert.ThrowsAsync<NfsException>(
            () => client.DeleteDirectoryAsync(nonEmptyDirectory, recursive: false, timeout.Token));
        Assert.Equal(NfsV3Status.NotEmpty, notEmpty.Status);

        await client.DeleteDirectoryAsync(nonEmptyDirectory, recursive: true, timeout.Token);
        await AssertMissingPathAsync(client, nonEmptyDirectory, timeout.Token);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesRenameSameDirectoryCrossDirectoryReplacementAndInvalidTargets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var sameSource = fixture.GetRunPath("same-source.txt");
        var sameTarget = fixture.GetRunPath("same-target.txt");
        await WriteBytesAsync(client, sameSource, [0x01, 0x02], timeout.Token);

        await client.MoveAsync(sameSource, sameTarget, timeout.Token);
        await AssertMissingPathAsync(client, sameSource, timeout.Token);
        Assert.Equal(new byte[] { 0x01, 0x02 }, await ReadBytesAsync(client, sameTarget, timeout.Token));

        var leftDirectory = fixture.GetRunPath("rename-left");
        var rightDirectory = fixture.GetRunPath("rename-right");
        await client.CreateDirectoryAsync(leftDirectory, timeout.Token);
        await client.CreateDirectoryAsync(rightDirectory, timeout.Token);

        var crossSource = $"{leftDirectory}/cross-source.txt";
        var crossTarget = $"{rightDirectory}/cross-target.txt";
        await WriteBytesAsync(client, crossSource, [0x03], timeout.Token);

        await client.MoveAsync(crossSource, crossTarget, timeout.Token);
        await AssertMissingPathAsync(client, crossSource, timeout.Token);
        Assert.Equal(new byte[] { 0x03 }, await ReadBytesAsync(client, crossTarget, timeout.Token));

        var replacementSource = fixture.GetRunPath("replacement-source.txt");
        var replacementTarget = fixture.GetRunPath("replacement-target.txt");
        await WriteBytesAsync(client, replacementSource, [0xAA, 0xBB], timeout.Token);
        await WriteBytesAsync(client, replacementTarget, [0xCC], timeout.Token);

        try
        {
            await client.MoveAsync(replacementSource, replacementTarget, timeout.Token);
            await AssertMissingPathAsync(client, replacementSource, timeout.Token);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, await ReadBytesAsync(client, replacementTarget, timeout.Token));
        }
        catch (NfsException ex) when (ex.Status == NfsV3Status.Io)
        {
            Assert.Equal(new byte[] { 0xAA, 0xBB }, await ReadBytesAsync(client, replacementSource, timeout.Token));
            Assert.Equal(new byte[] { 0xCC }, await ReadBytesAsync(client, replacementTarget, timeout.Token));
        }

        var missingSource = await Assert.ThrowsAsync<NfsException>(
            () => client.MoveAsync(fixture.GetRunPath("missing-source.txt"), fixture.GetRunPath("missing-target.txt"), timeout.Token));
        Assert.Equal(NfsV3Status.NoEnt, missingSource.Status);

        var invalidParentSource = fixture.GetRunPath("invalid-parent-source.txt");
        var fileAsParent = fixture.GetRunPath("file-as-parent.txt");
        await WriteBytesAsync(client, invalidParentSource, [0x11], timeout.Token);
        await WriteBytesAsync(client, fileAsParent, [0x22], timeout.Token);

        var notDirectory = await Assert.ThrowsAsync<NfsException>(
            () => client.MoveAsync(invalidParentSource, $"{fileAsParent}/child.txt", timeout.Token));
        Assert.Equal(NfsV3Status.NotDir, notDirectory.Status);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesSymbolicAndHardLinkCreationBehavior()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var source = fixture.GetRunPath("link-source.txt");
        await WriteBytesAsync(client, source, [0x48, 0x4C], timeout.Token);

        if (fixture.Capabilities.SupportsSymbolicLinks)
        {
            var symlink = fixture.GetRunPath("link-source-symlink");
            var created = await client.CreateSymLinkAsync(symlink, "link-source.txt", timeout.Token);
            AssertLookupAttributes(created, NfsType.Lnk);

            var target = await client.ReadLinkAsync(symlink, timeout.Token);
            Assert.Equal("link-source.txt", target);

            var lookup = await client.LookupPathAsync(symlink, timeout.Token);
            AssertLookupAttributes(lookup, NfsType.Lnk);
        }

        if (fixture.Capabilities.SupportsHardLinks)
        {
            var hardLink = fixture.GetRunPath("link-source-hardlink.txt");
            await client.CreateHardLinkAsync(source, hardLink, timeout.Token);

            var sourceAttributes = await client.GetAttributesAsync(source, timeout.Token);
            var linkAttributes = await client.GetAttributesAsync(hardLink, timeout.Token);
            Assert.Equal(sourceAttributes.FileId, linkAttributes.FileId);
            Assert.True(linkAttributes.LinkCount >= 2);

            await client.DeleteFileAsync(source, timeout.Token);
            await AssertMissingPathAsync(client, source, timeout.Token);
            Assert.Equal(new byte[] { 0x48, 0x4C }, await ReadBytesAsync(client, hardLink, timeout.Token));
        }
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_VerifiesAttributeMutationByPathAndHandle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var path = fixture.GetRunPath("attribute-mutation.txt");
        await WriteBytesAsync(client, path, [0x41, 0x42, 0x43, 0x44, 0x45], timeout.Token);

        var pathMtime = new DateTime(2024, 02, 03, 05, 06, 07, DateTimeKind.Utc);
        await client.SetAttributesAsync(
            path,
            new NfsSetAttributes
            {
                Mode = 0x180,
                Size = 3,
                Mtime = pathMtime
            },
            timeout.Token);

        var pathAttributes = await client.GetAttributesAsync(path, timeout.Token);
        Assert.Equal(0x180u, pathAttributes.Mode & 0x1FF);
        Assert.Equal(3, pathAttributes.Size);
        AssertCloseTo(pathMtime, pathAttributes.Mtime);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, await ReadBytesAsync(client, path, timeout.Token));
        Assert.NotNull(pathAttributes.CtimeTimestamp);

        await client.SetAttributesGuardedAsync(
            path,
            new NfsSetAttributes { Mode = 0x1A0 },
            pathAttributes.CtimeTimestamp.Value,
            timeout.Token);

        var guardedPathAttributes = await client.GetAttributesAsync(path, timeout.Token);
        Assert.Equal(0x1A0u, guardedPathAttributes.Mode & 0x1FF);

        var staleGuard = await Assert.ThrowsAsync<NfsException>(
            () => client.SetAttributesGuardedAsync(
                path,
                new NfsSetAttributes { Mode = 0x1FF },
                new NfsTimestamp(0, 0),
                timeout.Token));
        Assert.Equal(NfsV3Status.NotSync, staleGuard.Status);

        var lookup = await client.LookupPathAsync(path, timeout.Token);
        var handleMtime = new DateTime(2024, 03, 04, 06, 07, 08, DateTimeKind.Utc);
        await client.SetAttributesAsync(
            lookup.Handle,
            new NfsSetAttributes
            {
                Mode = 0x1A0,
                Size = 6,
                Mtime = handleMtime
            },
            timeout.Token);

        var handleAttributes = await client.GetAttributesAsync(path, timeout.Token);
        Assert.Equal(0x1A0u, handleAttributes.Mode & 0x1FF);
        Assert.Equal(6, handleAttributes.Size);
        AssertCloseTo(handleMtime, handleAttributes.Mtime);
        Assert.NotNull(handleAttributes.CtimeTimestamp);

        await client.SetAttributesGuardedAsync(
            lookup.Handle,
            new NfsSetAttributes { Mode = 0x180 },
            handleAttributes.CtimeTimestamp.Value,
            timeout.Token);

        handleAttributes = await client.GetAttributesAsync(path, timeout.Token);
        Assert.Equal(0x180u, handleAttributes.Mode & 0x1FF);

        await client.ChmodAsync(path, 0x1A4, timeout.Token);
        await client.ChownAsync(path, handleAttributes.Uid, handleAttributes.Gid, timeout.Token);
        await client.SetFileSizeAsync(path, 2, timeout.Token);
        await client.UtimesAsync(path, pathMtime, handleMtime, timeout.Token);

        var finalAttributes = await client.GetAttributesAsync(path, timeout.Token);
        Assert.Equal(0x1A4u, finalAttributes.Mode & 0x1FF);
        Assert.Equal(2, finalAttributes.Size);
        Assert.Equal(handleAttributes.Uid, finalAttributes.Uid);
        Assert.Equal(handleAttributes.Gid, finalAttributes.Gid);
        AssertCloseTo(pathMtime, finalAttributes.Atime);
        AssertCloseTo(handleMtime, finalAttributes.Mtime);
        Assert.Equal(new byte[] { 0x41, 0x42 }, await ReadBytesAsync(client, path, timeout.Token));
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsV3Client_PreservesSetAttributePermissionFailures()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);

        if (!fixture.Capabilities.AppliesRestrictedModeBits)
            return;

        var restrictedFile = await setupClient.LookupPathAsync(
            NfsV3IntegrationFixture.RestrictedFilePath,
            timeout.Token);
        var restrictedAttributes = await setupClient.GetAttributesAsync(restrictedFile.Handle, timeout.Token);
        var deniedUserId = restrictedAttributes.Uid == 65534 ? 65533u : 65534u;
        await using var deniedClient = await ConnectV3ClientAsync(
            userId: deniedUserId,
            groupId: 65534,
            timeout.Token);

        var deniedAccess = await deniedClient.AccessAsync(
            restrictedFile.Handle,
            NfsAccessMode.Modify,
            timeout.Token);
        if ((deniedAccess & NfsAccessMode.Modify) != 0)
            return;

        var denied = await Assert.ThrowsAsync<NfsException>(
            () => deniedClient.SetAttributesAsync(
                restrictedFile.Handle,
                new NfsSetAttributes { Mode = 0x1A4 },
                timeout.Token));

        Assert.Contains(denied.Status, new uint?[] { NfsV3Status.Access, NfsV3Status.Perm });
        Assert.Contains("SETATTR", denied.Message);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    public async Task NfsClient_MutatesAttributesThroughFacade()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions());

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);
        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);

        var path = fixture.GetRunPath("facade-attribute-mutation.txt");
        await using (var content = new MemoryStream([0x61, 0x62, 0x63, 0x64], writable: false))
        {
            await client.WriteAsync(path, content, timeout.Token);
        }

        var mtime = new DateTime(2024, 04, 05, 06, 07, 08, DateTimeKind.Utc);
        await client.SetAttributesAsync(
            path,
            new NfsSetAttributes
            {
                Mode = 0x180,
                Size = 3,
                Mtime = mtime
            },
            timeout.Token);

        var attributes = await client.GetItemAttributesAsync(path, timeout.Token);
        Assert.Equal(0x180u, attributes.Mode & 0x1FF);
        Assert.Equal(3, attributes.Size);
        AssertCloseTo(mtime, attributes.Mtime);
        Assert.NotNull(attributes.CtimeTimestamp);

        await client.SetAttributesGuardedAsync(
            path,
            new NfsSetAttributes { Mode = 0x1A0 },
            attributes.CtimeTimestamp.Value,
            timeout.Token);

        attributes = await client.GetItemAttributesAsync(path, timeout.Token);
        Assert.Equal(0x1A0u, attributes.Mode & 0x1FF);

        await client.ChmodAsync(path, 0x1A4, timeout.Token);
        await client.SetFileSizeAsync(path, 2, timeout.Token);

        attributes = await client.GetItemAttributesAsync(path, timeout.Token);
        Assert.Equal(0x1A4u, attributes.Mode & 0x1FF);
        Assert.Equal(2, attributes.Size);

        await using var output = new MemoryStream();
        await client.ReadAsync(path, output, timeout.Token);
        Assert.Equal(new byte[] { 0x61, 0x62 }, output.ToArray());
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
    public async Task NfsV3Client_ReconnectsAndRetriesAfterTransportFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(
            CreateOptions(maxRetries: 1, retryDelay: TimeSpan.Zero),
            timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var before = await client.GetAttributesAsync(fixture.RunDirectory, timeout.Token);
        await client.DisposeActiveNfsConnectionForTestingAsync();

        var after = await client.GetAttributesAsync(fixture.RunDirectory, timeout.Token);

        Assert.Equal(before.Type, after.Type);
        Assert.Equal(before.FileId, after.FileId);
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

        await client.DisposeAsync();
        await client.UnMountDeviceAsync(timeout.Token);
        Assert.False(client.IsMounted);
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
        int? maxWriteSize = null,
        NfsWriteStableHow stableHow = NfsWriteStableHow.FileSync,
        bool enableDirectoryCache = false,
        TimeSpan? directoryCacheTtl = null,
        int maxRetries = 0,
        TimeSpan? retryDelay = null,
        uint? userId = null,
        uint? groupId = null) =>
        new()
        {
            UserId = userId ?? NfsV3IntegrationEnvironment.UserId,
            GroupId = groupId ?? NfsV3IntegrationEnvironment.GroupId,
            UsePrivilegedSourcePort = false,
            PortmapPort = NfsV3IntegrationEnvironment.PortmapPort,
            CommandTimeout = TimeSpan.FromSeconds(10),
            MaxRetries = maxRetries,
            RetryDelay = retryDelay ?? TimeSpan.Zero,
            MaxReadSize = maxReadSize ?? NfsClientOptions.Default.MaxReadSize,
            MaxWriteSize = maxWriteSize ?? NfsClientOptions.Default.MaxWriteSize,
            StableHow = stableHow,
            ReaddirCount = readdirCount ?? NfsClientOptions.Default.ReaddirCount,
            EnableDirectoryCache = enableDirectoryCache,
            DirectoryCacheTtl = directoryCacheTtl ?? NfsClientOptions.Default.DirectoryCacheTtl
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

    private static async Task WriteBytesAsync(
        NfsV3Client client,
        string path,
        byte[] content,
        CancellationToken ct)
    {
        await using var input = new MemoryStream(content, writable: false);
        await client.WriteFileAsync(path, input, ct);
    }

    private static async Task<byte[]> ReadBytesAsync(
        NfsV3Client client,
        string path,
        CancellationToken ct)
    {
        await using var output = new MemoryStream();
        await client.ReadFileAsync(path, output, ct);
        return output.ToArray();
    }

    private static async Task AssertMissingPathAsync(
        NfsV3Client client,
        string path,
        CancellationToken ct)
    {
        await AssertNfsStatusAsync(
            NfsV3Status.NoEnt,
            isNotFound: true,
            "LOOKUP",
            () => client.LookupPathAsync(path, ct));
    }

    private static async Task<NfsException> AssertNfsStatusAsync(
        uint expectedStatus,
        bool isNotFound,
        string messageFragment,
        Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<NfsException>(action);
        Assert.Equal(expectedStatus, exception.Status);
        Assert.Equal(isNotFound, exception.IsNotFound);
        Assert.Contains(messageFragment, exception.Message);
        Assert.Contains(NfsV3Status.Describe(expectedStatus), exception.Message);
        Assert.Null(exception.InnerException);
        return exception;
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

    private static void AssertWriteResult(NfsWriteResult result)
    {
        Assert.True(result.Count > 0);
        Assert.Contains(result.Committed, Enum.GetValues<NfsWriteStableHow>());
        Assert.Equal(8, result.WriteVerifier.Length);
    }

    private static void AssertCommittedAtLeast(NfsWriteStableHow requested, NfsWriteStableHow actual) =>
        Assert.True(
            (uint)actual >= (uint)requested,
            $"Expected committed stability {actual} to be at least requested stability {requested}.");

    private static void AssertCommitResult(NfsCommitResult result) =>
        Assert.Equal(8, result.WriteVerifier.Length);

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

    private sealed class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
