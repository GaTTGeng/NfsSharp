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
        if (!OperatingSystem.IsLinux())
            return;

        using var first = options.GssMechanismFactory();
        using var second = options.GssMechanismFactory();
        Assert.NotSame(first, second);
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
