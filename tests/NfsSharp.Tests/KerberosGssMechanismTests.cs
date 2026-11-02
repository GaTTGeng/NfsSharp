using NfsSharp.Protocol;
using NfsSharp.Client;

namespace NfsSharp.Tests;

/// <summary>Checks the platform boundary and advertised capabilities of the MIT GSSAPI mechanism.</summary>
public sealed class KerberosGssMechanismTests
{
    [Fact]
    public void TokenVerification_AcceptsValidOutOfOrderTokensButRejectsReplayStatuses()
    {
        Assert.True(KerberosGssMechanism.IsSuccessfulTokenVerification(0));
        Assert.True(KerberosGssMechanism.IsSuccessfulTokenVerification(1 << 3));
        Assert.True(KerberosGssMechanism.IsSuccessfulTokenVerification(1 << 4));
        Assert.True(KerberosGssMechanism.IsSuccessfulTokenVerification((1 << 3) | (1 << 4)));
        Assert.False(KerberosGssMechanism.IsSuccessfulTokenVerification(1 << 1));
        Assert.False(KerberosGssMechanism.IsSuccessfulTokenVerification(1 << 2));
        Assert.False(KerberosGssMechanism.IsSuccessfulTokenVerification(0x0006_0000));
    }

    [Fact]
    public void WithKerberos_UsesAFactoryForIndependentSessionMechanisms()
    {
        var options = new NfsClientBuilder().WithKerberos("nfs/server.example").BuildOptions();

        Assert.Null(options.GssMechanism);
        Assert.NotNull(options.GssMechanismFactory);
        Assert.True(options.RequiresLinuxGssMechanism);
        if (!OperatingSystem.IsLinux())
            return;

        using var first = options.GssMechanismFactory();
        using var second = options.GssMechanismFactory();
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithKerberos_RejectsEmptyTarget(string? targetSpn)
    {
        Assert.ThrowsAny<ArgumentException>(() => new NfsClientBuilder().WithKerberos(targetSpn!));
    }

    [Fact]
    public async Task NfsV3Client_RejectsKerberosPlatformBeforeResolvingServer()
    {
        if (OperatingSystem.IsLinux())
            return;

        var options = new NfsClientBuilder().WithKerberos("nfs/server.example").BuildOptions();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => NfsV3Client.ConnectAsync(
                "server-that-must-not-be-resolved.invalid", "/export", options, CancellationToken.None));
    }

    [Fact]
    public async Task NfsV3Client_RejectsExplicitKerberosCredentialsBeforeResolvingServer()
    {
        var options = new NfsClientBuilder()
            .WithKerberos("nfs/server.example")
            .WithGssCredentials(new GssCredentials { UserName = "user", Password = "password" })
            .BuildOptions();

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ConnectAsync(
                "server-that-must-not-be-resolved.invalid", "/export", options, CancellationToken.None));

        Assert.Contains("WithKerberos does not support explicit GssCredentials", exception.Message);
    }

    [Fact]
    public void Constructor_RequiresLinux()
    {
        if (OperatingSystem.IsLinux())
        {
            using var mechanism = new KerberosGssMechanism();
            Assert.Equal(new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02 }, mechanism.MechanismOid);
            Assert.True(mechanism.CanComputeMic);
            Assert.True(mechanism.ProvidesCryptographicProtection);
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => new KerberosGssMechanism());
    }
}
