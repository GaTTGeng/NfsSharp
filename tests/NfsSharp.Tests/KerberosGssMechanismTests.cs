using NfsSharp.Protocol;

namespace NfsSharp.Tests;

/// <summary>Checks the platform boundary and advertised capabilities of the MIT GSSAPI mechanism.</summary>
public sealed class KerberosGssMechanismTests
{
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
