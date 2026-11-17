using NfsSharp.Client;
using NfsSharp.Protocol;

namespace NfsSharp.Tests;

/// <summary>Exercises RPCSEC_GSS against the isolated MIT Kerberos and NFS-Ganesha fixture.</summary>
public sealed class KerberosIntegrationTests
{
    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ConnectsAndTransfersData(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new NfsClientBuilder()
            .WithKerberos("nfs/nfs.test.local@TEST.LOCAL", service)
            .WithPrivilegedSourcePort(false)
            .BuildOptions();
        await using var client = await NfsV3Client.ConnectAsync("127.0.0.1", "/export", options, timeout.Token);
        Assert.Equal(NfsType.Dir, (await client.GetAttributesAsync(client.RootHandle, timeout.Token)).Type);

        var path = $"kerberos-{Guid.NewGuid():N}";
        var payload = new byte[] { 0, 1, 2, 3, 255 };
        await using var input = new MemoryStream(payload);
        await client.WriteFileAsync(path, input, timeout.Token);
        await using var output = new MemoryStream();
        await client.ReadFileAsync(path, output, timeout.Token);
        Assert.Equal(payload, output.ToArray());
        await client.DeleteFileAsync(path, timeout.Token);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ReadOnlyExportRejectsCreate(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new NfsClientBuilder()
            .WithKerberos("nfs/nfs.test.local@TEST.LOCAL", service)
            .WithPrivilegedSourcePort(false)
            .BuildOptions();
        await using var client = await NfsV3Client.ConnectAsync("127.0.0.1", "/readonly", options, timeout.Token);
        Assert.Equal(NfsType.Dir, (await client.GetAttributesAsync(client.RootHandle, timeout.Token)).Type);

        await using var input = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<NfsException>(() =>
            client.WriteFileAsync($"denied-{Guid.NewGuid():N}", input, timeout.Token));
        Assert.Equal(NfsV3Status.RoFs, error.Status);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task RejectsUnknownServicePrincipal(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new NfsClientBuilder()
            .WithKerberos("nfs/missing.test.local@TEST.LOCAL", service)
            .WithPrivilegedSourcePort(false)
            .BuildOptions();
        await Assert.ThrowsAsync<NfsException>(() =>
            NfsV3Client.ConnectAsync("127.0.0.1", "/export", options, timeout.Token));
    }
}

/// <summary>Enables the disposable Kerberos integration fixture explicitly.</summary>
public sealed class KerberosIntegrationTheoryAttribute : TheoryAttribute
{
    public KerberosIntegrationTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("NFSSHARP_RUN_KERBEROS_INTEGRATION") != "1")
            Skip = "Set NFSSHARP_RUN_KERBEROS_INTEGRATION=1 to run Kerberos integration tests.";
    }
}
