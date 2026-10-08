using NfsSharp.Client;
using NfsSharp.Protocol;
using Xunit.Abstractions;

namespace NfsSharp.Tests;

/// <summary>
/// End-to-end NFSv3 tests against a real server. Opt-in only: each fact is skipped unless
/// NFSSHARP_RUN_NFSV3_INTEGRATION=1 is set (see NfsV3IntegrationFactAttribute), with the target
/// server/export/uid/gid supplied via the NFSSHARP_NFS_* environment variables.
/// </summary>
public sealed class NfsV3IntegrationTests
{
    private const string MissingExportPath = "/missing-export";
    private readonly ITestOutputHelper _output;

    public NfsV3IntegrationTests(ITestOutputHelper output) => _output = output;

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
    // After unmount the connection is unusable: further RPC calls must throw rather than silently reconnect.
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
            // CREATE returns a usable handle; LOOKUP must resolve the same directory back to a handle.
            var created = await client.CreateDirectoryAsync(directory, timeout.Token);
            Assert.NotEmpty(created.Handle);

            var lookup = await client.LookupPathAsync(directory, timeout.Token);
            Assert.NotEmpty(lookup.Handle);

            var attributes = await client.GetAttributesAsync(directory, timeout.Token);
            Assert.Equal(NfsType.Dir, attributes.Type);
            Assert.True(attributes.Mode > 0);
            Assert.True(attributes.FileId > 0);

            // Existence helpers agree with the object type: the directory is present, its child path is not.
            Assert.True(await client.FileExistsAsync(directory, timeout.Token));
            Assert.True(await client.IsDirectoryAsync(directory, timeout.Token));
            Assert.False(await client.FileExistsAsync($"{directory}/missing", timeout.Token));

            // The export root listing must include the shared fixture tree.
            var entries = await client.ReadDirAsync(".", timeout.Token);
            Assert.Contains(entries, entry => entry.Name == NfsV3IntegrationFixture.RootDirectory);

            // READDIRPLUS supplies attributes and a handle for the new directory in one round trip.
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
    // Confirms the shared fixture tree materializes correctly, including optional symlink/hard-link/restricted-mode features when the server supports them.
    public async Task NfsV3Client_MaterializesDeterministicFixtureData()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        // Required tree: three directories and five files, each checked for type, size, mode, and content.
        await AssertDirectoryAsync(client, NfsV3IntegrationFixture.RootDirectory, timeout.Token);
        await AssertDirectoryAsync(client, NfsV3IntegrationFixture.EmptyDirectory, timeout.Token);
        await AssertDirectoryAsync(client, NfsV3IntegrationFixture.NestedDirectory, timeout.Token);

        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.EmptyFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.SmallFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.NestedFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.UnicodeFile, timeout.Token);
        await AssertFixtureFileAsync(client, NfsV3IntegrationFixture.BoundaryFile, timeout.Token);

        // Optional features are asserted only when the fixture probed them as supported.
        if (fixture.Capabilities.SupportsSymbolicLinks)
        {
            var target = await client.ReadLinkAsync(NfsV3IntegrationFixture.SymlinkPath, timeout.Token);
            Assert.Equal(NfsV3IntegrationFixture.SymlinkTarget, target);
        }

        // A hard link shares the source fileid and bumps its link count to at least two.
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

        // LOOKUP of "." must resolve to the mount's root handle with directory attributes.
        var rootLookup = await client.LookupPathAsync(".", timeout.Token);
        Assert.Equal(client.RootHandle, rootLookup.Handle);
        Assert.Equal(NfsType.Dir, rootLookup.Attr?.Type);

        // Path-based LOOKUP and handle-based GETATTR must agree on fileid and mode for the fixture root.
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

        // File attributes cover size, mode, and the fixture's forced mtime; atime/ctime must be present.
        var fileLookup = await client.LookupPathAsync(NfsV3IntegrationFixture.SmallFilePath, timeout.Token);
        AssertLookupAttributes(fileLookup, NfsType.Reg);
        var fileByHandle = await client.GetAttributesAsync(fileLookup.Handle, timeout.Token);
        Assert.Equal(NfsV3IntegrationFixture.SmallFile.Size, fileByHandle.Size);
        Assert.Equal(NfsV3IntegrationFixture.SmallFile.Mode, fileByHandle.Mode & 0x1FF);
        Assert.Equal(fileLookup.Attr!.FileId, fileByHandle.FileId);
        AssertCloseTo(NfsV3IntegrationFixture.TimestampUtc, fileByHandle.Mtime);
        Assert.NotNull(fileByHandle.Atime);
        Assert.NotNull(fileByHandle.Ctime);

        // ACCESS is expected to grant the full requested mask on the fixture file and directory.
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

        // Requesting no access is valid and must report an empty grant rather than fail.
        var noAccessRequested = await client.AccessAsync(
            fileLookup.Handle,
            NfsAccessMode.None,
            timeout.Token);
        Assert.Equal(NfsAccessMode.None, noAccessRequested);

        // A mask bit outside the NFS3 access flags is a client-side validation error (no NFS status).
        var invalidAccess = await Assert.ThrowsAsync<NfsException>(
            () => client.AccessAsync(
                fileLookup.Handle,
                (NfsAccessMode)0x8000,
                timeout.Token));
        Assert.Contains("Invalid ACCESS mask", invalidAccess.Message);

        // Lookup failures: a missing name is NFS3ERR_NOENT, a non-directory parent is NFS3ERR_NOTDIR.
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

        // Arrange a small conflict tree: an existing file, an empty dir, a non-empty dir, and a handle to be made stale.
        var existingFile = fixture.GetRunPath("existing-file.txt");
        var existingDirectory = fixture.GetRunPath("existing-dir");
        var nonEmptyDirectory = fixture.GetRunPath("non-empty-dir");
        var staleFile = fixture.GetRunPath("stale-file.txt");

        await WriteBytesAsync(client, existingFile, [0x01], timeout.Token);
        await client.CreateDirectoryAsync(existingDirectory, timeout.Token);
        await client.CreateDirectoryAsync(nonEmptyDirectory, timeout.Token);
        await WriteBytesAsync(client, $"{nonEmptyDirectory}/child.txt", [0x02], timeout.Token);

        // Each case asserts the NFS3 status and whether it maps to IsNotFound on the public exception.
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

        // REMOVE on a directory and RMDIR on a file are type mismatches (ISDIR / NOTDIR respectively).
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

        // Delete a file and keep its handle: subsequent GETATTR/ACCESS must report STALE, not NOENT.
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

        // A 256-byte name exceeds the usual component limit and is rejected client-side without an NFS status.
        var tooLongName = new string('x', 256);
        var tooLong = await Assert.ThrowsAsync<NfsException>(
            () => client.CreateFileAsync($"{fixture.RunDirectory}/{tooLongName}", timeout.Token));
        Assert.Null(tooLong.Status);
        Assert.False(tooLong.IsNotFound);
        Assert.Contains("too long", tooLong.Message);

        if (!fixture.Capabilities.AppliesRestrictedModeBits)
            return;

        // An unprivileged uid/gid must be denied on the mode-0 restricted path with ACCESS or PERM.
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
    // Forces a small readdir count so cookie-based pagination is actually exercised across multiple pages.
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
        // Directory caching is on with a long TTL; every mutation below must invalidate the cached listing.
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

        // SETATTR size change must be visible on the next listing despite the long cache TTL.
        await client.SetFileSizeAsync(path, 4, timeout.Token);

        entries = await client.ReadDirPlusAsync(fixture.RunDirectory, timeout.Token);
        Assert.Equal(4L, AssertContainsEntry(entries, "cache-coherence.txt").Attr?.Size);

        // A content rewrite reports the new size; the returned handle is then corrupted to prove cached entries are clones.
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

        // CREATE then RENAME then REMOVE each update the listing: new name appears, old name disappears twice.
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
    // A second client must not see stale directory-cache entries after another client mutates the export.
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

        // Empty file: any read at offset 0 returns zero bytes and eof.
        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.EmptyFile,
            offset: 0,
            count: 8,
            expectedEof: true,
            ct: timeout.Token);

        // Mid-file read: short of the end, so eof must stay false.
        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.SmallFile,
            offset: 5,
            count: 7,
            expectedEof: false,
            ct: timeout.Token);

        // Read starting at the final byte: one byte returned, then eof.
        await AssertReadAtAsync(
            client,
            NfsV3IntegrationFixture.SmallFile,
            offset: (ulong)NfsV3IntegrationFixture.SmallFile.Size - 1,
            count: 16,
            expectedEof: true,
            ct: timeout.Token);

        // Boundary file: a request spanning the last 7 bytes is short relative to count but still eof.
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
    public async Task NfsV3Client_ConcurrentReadAtCallsShareConnectionAndReturnMatchingData()
    {
        // 32 concurrent calls share one connection; each read targets a distinct offset/size pair on either file.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(
            CreateOptions() with { MaxOutstandingRpcCallsPerConnection = 32 },
            timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);
        var small = await client.LookupPathAsync(NfsV3IntegrationFixture.SmallFile.Path, timeout.Token);
        var large = await client.LookupPathAsync(NfsV3IntegrationFixture.BoundaryFile.Path, timeout.Token);

        var reads = Enumerable.Range(0, 32).Select(async index =>
        {
            // Alternate between the two files and vary the request size so replies cannot be cross-wired.
            var file = index % 2 == 0 ? NfsV3IntegrationFixture.SmallFile : NfsV3IntegrationFixture.BoundaryFile;
            var handle = index % 2 == 0 ? small.Handle : large.Handle;
            var count = Math.Min(index % 2 == 0 ? 16 : 1024, file.Content.Length);
            var offset = file.Content.Length <= count ? 0 : (index * 97) % (file.Content.Length - count + 1);
            var buffer = new byte[count];
            var (bytesRead, eof) = await client.ReadAtAsync(
                handle,
                (ulong)offset,
                buffer,
                0,
                count,
                timeout.Token);

            // Each caller must see exactly its own slice of bytes and the correct eof for that offset.
            Assert.Equal(count, bytesRead);
            Assert.Equal(file.Content.AsSpan(offset, count).ToArray(), buffer);
            Assert.Equal(offset + count == file.Content.Length, eof);
        });

        await Task.WhenAll(reads);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Benchmark")]
    // Measures that concurrent reads at 1/8/32 callers all return the correct bytes without cross-call data mixing.
    public async Task NfsV3Client_ReportsConcurrentReadLoadAtOneEightAndThirtyTwoCallers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var setupClient = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(setupClient, timeout.Token);
        var small = await setupClient.LookupPathAsync(NfsV3IntegrationFixture.SmallFile.Path, timeout.Token);
        // A 256 KiB deterministic file gives the load test a large-payload scenario alongside the small fixture file.
        var largeContent = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var largePath = fixture.GetRunPath("rpc-load-large.bin");
        await using (var input = new MemoryStream(largeContent, writable: false))
            await setupClient.WriteFileAsync(largePath, input, timeout.Token);
        var large = await setupClient.LookupPathAsync(largePath, timeout.Token);
        var scenarios = new[]
        {
            (Name: "small", Handle: small.Handle, Content: NfsV3IntegrationFixture.SmallFile.Content, RequestedLength: 64),
            (Name: "large", Handle: large.Handle, Content: largeContent, RequestedLength: 64 * 1024)
        };

        foreach (var concurrency in new[] { 1, 8, 32 })
        {
            await using var client = await ConnectV3ClientAsync(
                CreateOptions() with { MaxOutstandingRpcCallsPerConnection = concurrency },
                timeout.Token);

            foreach (var scenario in scenarios)
            {
                // 64 reads per scenario; latency and allocation are sampled around the whole batch.
                const int operations = 64;
                var payloadSize = Math.Min(scenario.RequestedLength, scenario.Content.Length);
                var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var wall = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, operations).Select(async index =>
                {
                    var count = payloadSize;
                    // Large-file reads scatter their offsets so concurrent replies are compared against distinct slices.
                    var offset = scenario.Name == "large"
                        ? index * 13 % (scenario.Content.Length - count + 1)
                        : 0;
                    var buffer = new byte[count];
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    var (bytesRead, _) = await client.ReadAtAsync(
                        scenario.Handle,
                        (ulong)offset,
                        buffer,
                        0,
                        count,
                        timeout.Token);
                    elapsed.Stop();
                    Assert.Equal(count, bytesRead);
                    latencies.Add(elapsed.Elapsed.TotalMilliseconds);
                }));
                wall.Stop();

                var sorted = latencies.Order().ToArray();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                _output.WriteLine(
                "rpc-load server={0} auth=AUTH_SYS concurrency={1} payload={2} bytes={3} operations={4} elapsed_ms={5:F1} ops_per_second={6:F1} p50_ms={7:F2} p95_ms={8:F2} allocated_bytes={9} worker_nfs_sockets=1 pending_high_water={10}",
                    NfsV3IntegrationEnvironment.Server,
                    concurrency,
                    scenario.Name,
                    payloadSize,
                    operations,
                    wall.Elapsed.TotalMilliseconds,
                    operations / wall.Elapsed.TotalSeconds,
                    Percentile(sorted, 0.50),
                    Percentile(sorted, 0.95),
                    allocated,
                    client.RpcPendingCallHighWaterMarkForTesting);
            }
        }
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

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

        // Pre-canceled tokens surface as OperationCanceledException before any RPC is attempted.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReadAtAsync(lookup.Handle, 0, buffer, 0, buffer.Length, canceled.Token));

        // Client-side argument validation: empty handle, negative buffer offset, and an out-of-range slice.
        var invalidHandle = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadAtAsync(Array.Empty<byte>(), 0, buffer, 0, buffer.Length, timeout.Token));
        Assert.Contains("file handle is empty", invalidHandle.Message);

        var negativeOffset = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadAtAsync(lookup.Handle, 0, buffer, -1, buffer.Length, timeout.Token));
        Assert.Contains("Buffer offset", negativeOffset.Message);

        var tooLargeRange = await Assert.ThrowsAsync<NfsException>(
            () => client.ReadAtAsync(lookup.Handle, 0, buffer, 8, buffer.Length, timeout.Token));
        Assert.Contains("exceed the buffer length", tooLargeRange.Message);

        // A zero-length read is a no-op and does not report eof.
        var zeroLengthRead = await client.ReadAtAsync(lookup.Handle, 0, buffer, 0, 0, timeout.Token);
        Assert.Equal(0, zeroLengthRead.BytesRead);
        Assert.False(zeroLengthRead.Eof);

        // Requests above MaxReadSize are rejected before the READ RPC is sent.
        await using var limitedClient = await ConnectV3ClientAsync(
            CreateOptions(maxReadSize: 4),
            timeout.Token);
        var tooLargeRead = await Assert.ThrowsAsync<NfsException>(
            () => limitedClient.ReadAtAsync(lookup.Handle, 0, buffer, 0, 5, timeout.Token));
        Assert.Contains("exceeds MaxReadSize", tooLargeRead.Message);

        // ReadFileAsync on a missing remote path reports NOENT; a local destination failure must not leave partial output.
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

        // WriteAt path: create, write at offset 0, overwrite overlapping bytes at offset 2, then commit twice.
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

        // COMMIT ranges may be partial (offset 2, count 3) as long as they stay within the written data.
        var commit = await client.CommitWithResultAsync(created.Handle, 0, 0, timeout.Token);
        AssertCommitResult(commit);
        commit = await client.CommitWithResultAsync(created.Handle, 2, 3, timeout.Token);
        AssertCommitResult(commit);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x20, 0x21, 0x22, 0x23 }, await ReadBytesAsync(client, offsetPath, timeout.Token));

        // WriteFile path: stream content creates the file and reports its post-write size attribute.
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

        // Rewriting the same path must replace the content and shrink the reported size.
        var replacementContent = new byte[] { 0x55, 0x56, 0x57 };
        await using (var input = new MemoryStream(replacementContent, writable: false))
        {
            streamLookup = await client.WriteFileAsync(streamPath, input, timeout.Token);
        }

        Assert.NotNull(streamLookup.Attr);
        Assert.Equal(replacementContent.Length, streamLookup.Attr.Size);
        Assert.Equal(replacementContent, await ReadBytesAsync(client, streamPath, timeout.Token));

        // A small MaxWriteSize forces WriteFile to split the stream across several WRITE RPCs.
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

        // A zero-length write is legal and carries no writeverf.
        var zeroLengthWrite = await client.WriteAtAsync(created.Handle, 0, ReadOnlyMemory<byte>.Empty, timeout.Token);
        Assert.Equal(0, zeroLengthWrite);
        var zeroLengthWriteResult = await client.WriteAtWithResultAsync(created.Handle, 0, ReadOnlyMemory<byte>.Empty, timeout.Token);
        Assert.Equal(0, zeroLengthWriteResult.Count);
        Assert.Empty(zeroLengthWriteResult.WriteVerifier);

        // Write larger than the configured limit is rejected client-side before any RPC is sent.
        await using var limitedClient = await ConnectV3ClientAsync(
            CreateOptions(maxWriteSize: 2),
            timeout.Token);
        var tooLargeWrite = await Assert.ThrowsAsync<NfsException>(
            () => limitedClient.WriteAtAsync(created.Handle, 0, new byte[] { 0x01, 0x02, 0x03 }, timeout.Token));
        Assert.Contains("exceeds MaxWriteSize", tooLargeWrite.Message);

        // An unreadable input stream fails without creating the target path.
        var rejectedPath = fixture.GetRunPath("non-readable-stream.bin");
        await using var nonReadableInput = new NonReadableStream();
        var notReadable = await Assert.ThrowsAsync<NfsException>(
            () => client.WriteFileAsync(rejectedPath, nonReadableInput, timeout.Token));
        Assert.Contains("readable", notReadable.Message);
        await AssertMissingPathAsync(client, rejectedPath, timeout.Token);
    }

    [NfsV3IntegrationFact]
    [Trait("Category", "Integration")]
    // A canceled write must not leave a created file or report success; follow-up reads must fail or return no data.
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
        // The high-level facade is used here; MaxWriteSize=2 keeps individual WRITE RPCs small.
        await using var client = new NfsClient(NfsVersion.V3, CreateOptions(maxWriteSize: 2));

        await client.ConnectAsync(NfsV3IntegrationEnvironment.Server, timeout.Token);
        await client.MountDeviceAsync(NfsV3IntegrationEnvironment.ExportPath, timeout.Token);

        // Two disjoint writes leave a hole at offsets 2-3; the server zero-fills the gap on readback.
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

        // A stream write through the facade replaces the whole file contents.
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

        // MKDIR via handle+name with an initial mode; GETATTR must reflect the requested mode bits.
        var createdDirectory = await client.CreateDirectoryAsync(
            parent.Handle,
            "created-dir",
            new NfsSetAttributes { Mode = 0x1C0 },
            timeout.Token);
        AssertLookupAttributes(createdDirectory, NfsType.Dir);

        var directoryAttributes = await client.GetAttributesAsync(directory, timeout.Token);
        Assert.Equal(NfsType.Dir, directoryAttributes.Type);
        Assert.Equal(0x1C0u, directoryAttributes.Mode & 0x1FF);

        // CREATE via handle+name yields a regular file whose mode comes from the attribute argument.
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

        // REMOVE makes the name disappear and turns the retained handle STALE.
        await client.DeleteFileAsync(file, timeout.Token);
        await AssertMissingPathAsync(client, file, timeout.Token);
        var deletedHandle = await Assert.ThrowsAsync<NfsException>(
            () => client.GetAttributesAsync(createdFile.Handle, timeout.Token));
        Assert.Equal(NfsV3Status.Stale, deletedHandle.Status);

        // An empty directory deletes cleanly with recursive: false.
        await client.CreateDirectoryAsync(emptyDirectory, timeout.Token);
        await client.DeleteDirectoryAsync(emptyDirectory, recursive: false, timeout.Token);
        await AssertMissingPathAsync(client, emptyDirectory, timeout.Token);

        // A non-empty directory refuses a non-recursive RMDIR (NOT_EMPTY) but succeeds when recursive.
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
    // Server-dependent case: RENAME over an existing target either replaces it or fails with NFS3ERR_IO while both names survive (see NfsV3ReplacementRenameOutcome).
    public async Task NfsV3Client_VerifiesRenameSameDirectoryCrossDirectoryReplacementAndInvalidTargets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectV3ClientAsync(timeout.Token);
        await using var fixture = await NfsV3IntegrationFixture.CreateAsync(client, timeout.Token);

        var sameSource = fixture.GetRunPath("same-source.txt");
        var sameTarget = fixture.GetRunPath("same-target.txt");
        await WriteBytesAsync(client, sameSource, [0x01, 0x02], timeout.Token);

        // Same-directory rename: source disappears, target holds the original bytes.
        await client.MoveAsync(sameSource, sameTarget, timeout.Token);
        await AssertMissingPathAsync(client, sameSource, timeout.Token);
        Assert.Equal(new byte[] { 0x01, 0x02 }, await ReadBytesAsync(client, sameTarget, timeout.Token));

        // Cross-directory rename moves the file between two run subdirectories.
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

        // Replacement rename is server-dependent: either the target is replaced, or the server returns
        // NFS3ERR_IO and both names keep their original contents.
        var replacementSource = fixture.GetRunPath("replacement-source.txt");
        var replacementTarget = fixture.GetRunPath("replacement-target.txt");
        await WriteBytesAsync(client, replacementSource, [0xAA, 0xBB], timeout.Token);
        await WriteBytesAsync(client, replacementTarget, [0xCC], timeout.Token);

        var replacementOutcome = NfsV3ReplacementRenameOutcome.ReplaceTarget;
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
            replacementOutcome = NfsV3ReplacementRenameOutcome.IoPreservesBoth;
        }

        // When the environment pins the expected outcome, enforce it; otherwise accept either valid behavior.
        var expectedReplacementOutcome =
            NfsV3IntegrationEnvironment.ExpectedReplacementRenameOutcome;
        if (expectedReplacementOutcome != NfsV3ReplacementRenameOutcome.Unspecified)
            Assert.Equal(expectedReplacementOutcome, replacementOutcome);

        // Invalid rename targets: missing source is NOENT; a file used as the target parent is NOTDIR.
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

        // Symlink: creation yields a link object; READLINK returns the stored target text unchanged.
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

        // Hard link: both names share a fileid, and the data survives after the original name is removed.
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

        // Path-based SETATTR updates mode, size, and mtime together; size 3 truncates the content.
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

        // Guarded SETATTR succeeds only while the recorded ctime matches (weak cache consistency).
        await client.SetAttributesGuardedAsync(
            path,
            new NfsSetAttributes { Mode = 0x1A0 },
            pathAttributes.CtimeTimestamp.Value,
            timeout.Token);

        var guardedPathAttributes = await client.GetAttributesAsync(path, timeout.Token);
        Assert.Equal(0x1A0u, guardedPathAttributes.Mode & 0x1FF);

        // A guard of (0, 0) cannot match any ctime, so the server answers NFS3ERR_NOT_SYNC.
        var staleGuard = await Assert.ThrowsAsync<NfsException>(
            () => client.SetAttributesGuardedAsync(
                path,
                new NfsSetAttributes { Mode = 0x1FF },
                new NfsTimestamp(0, 0),
                timeout.Token));
        Assert.Equal(NfsV3Status.NotSync, staleGuard.Status);

        // The same mutations applied through a file handle must behave identically to the path-based calls.
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

        // Convenience wrappers each touch one field; the final attributes combine all of them.
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
    // Uses an unprivileged uid/gid connection so the server rejects SETATTR with NFS3ERR_ACCES/PERM.
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

        // Facade SETATTR mirrors the protocol client: mode, size, and mtime in one call, then readback via GetItemAttributesAsync.
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

        // Guarded update uses the ctime snapshot returned by the previous GETATTR.
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

        // Size 2 truncates the four-byte file to its first two bytes.
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
    // The client is expected to reconnect and retry a retry-safe call after the transport is torn down underneath it.
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
    // REMOUNT replaces the active export in place; Dispose must leave the facade unmounted and cleaned up.
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

    // Connect helpers parameterize uid/gid, readdir count, or options against the configured integration server.
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

    // The server may report a stronger stability guarantee than requested (e.g. FILE_SYNC when UNSTABLE was asked for).
    private static void AssertCommittedAtLeast(NfsWriteStableHow requested, NfsWriteStableHow actual) =>
        Assert.True(
            (uint)actual >= (uint)requested,
            $"Expected committed stability {actual} to be at least requested stability {requested}.");

    private static void AssertCommitResult(NfsCommitResult result) =>
        Assert.Equal(8, result.WriteVerifier.Length);

    // NFS timestamps have one-second resolution on some servers; allow a small delta when comparing against the fixture clock.
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
        // Space accounting must nest: available <= free <= total. Servers that report a zero total
        // are expected to report zero free/available as well rather than inventing values.
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

        // The same nesting rule applies to the file-slot counters.
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

        // Preferred and multiple sizes must fit inside their corresponding maximums.
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

        // MaxFileSize must accommodate the fixture's boundary file; unknown property bits are rejected.
        Assert.True(info.PreferredReaddirSize > 0);
        Assert.True(info.MaxFileSize >= (ulong)NfsV3IntegrationFixture.BoundaryFile.Size);
        Assert.True(info.TimeDelta >= TimeSpan.Zero);
        Assert.Equal(0u, info.Properties & ~KnownFsInfoProperties);

        // Property flags must agree with the capabilities the fixture probed at setup time.
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

        // NameMax must fit the fixture's longest name; at least one of the case flags must be set.
        Assert.True(pathConf.NameMax >= NfsV3IntegrationFixture.BoundaryFileName.Length);
        Assert.True(pathConf.CaseInsensitive || pathConf.CasePreserving);
    }

    private static async Task AssertPathConfCaseBehaviorAsync(
        NfsV3Client client,
        NfsV3IntegrationFixture fixture,
        NfsPathConf pathConf,
        CancellationToken ct)
    {
        // Create a mixed-case name, then verify the server's case flags describe real lookup behavior.
        var name = "PathConf-MixedCase.txt";
        var path = fixture.GetRunPath(name);
        await using (var content = new MemoryStream([0x43], writable: false))
        {
            await client.WriteFileAsync(path, content, ct);
        }

        // Case-preserving servers must echo the exact mixed-case spelling in READDIR.
        var entries = await client.ReadDirAsync(fixture.RunDirectory, ct);
        if (pathConf.CasePreserving)
            Assert.Contains(entries, entry => entry.Name == name);

        // Case-insensitive servers resolve the lower-case form to the same fileid; others must report NOENT.
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
