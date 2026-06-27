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
        var directory = CreateUniquePath("metadata");

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
            Assert.Contains(entries, entry => entry.Name == directory);

            var plusEntries = await client.ReadDirPlusAsync(".", timeout.Token);
            var plusEntry = Assert.Single(plusEntries, entry => entry.Name == directory);
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

    private static NfsClientOptions CreateOptions() =>
        new()
        {
            UserId = NfsV3IntegrationEnvironment.UserId,
            GroupId = NfsV3IntegrationEnvironment.GroupId,
            UsePrivilegedSourcePort = false,
            CommandTimeout = TimeSpan.FromSeconds(10),
            MaxRetries = 0
        };

    private static Task<NfsV3Client> ConnectV3ClientAsync(CancellationToken ct) =>
        NfsV3Client.ConnectAsync(
            NfsV3IntegrationEnvironment.Server,
            NfsV3IntegrationEnvironment.ExportPath,
            CreateOptions(),
            ct);

    private static string CreateUniquePath(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";

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
