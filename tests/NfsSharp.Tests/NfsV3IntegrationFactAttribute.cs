namespace NfsSharp.Tests;

public sealed class NfsV3IntegrationFactAttribute : FactAttribute
{
    public NfsV3IntegrationFactAttribute()
    {
        if (!NfsV3IntegrationEnvironment.IsEnabled)
            Skip = "Set NFSSHARP_RUN_NFSV3_INTEGRATION=1 to run real-server NFSv3 tests.";
    }
}

internal static class NfsV3IntegrationEnvironment
{
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("NFSSHARP_RUN_NFSV3_INTEGRATION"),
            "1",
            StringComparison.Ordinal);

    public static string Server =>
        Environment.GetEnvironmentVariable("NFSSHARP_NFS_SERVER") ?? "127.0.0.1";

    public static string ExportPath =>
        Environment.GetEnvironmentVariable("NFSSHARP_NFS_EXPORT") ?? "/export";

    public static string? ExpectedExportGroup
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("NFSSHARP_NFS_EXPECTED_EXPORT_GROUP");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            return UsesDefaultExportEndpoint ? "*" : null;
        }
    }

    public static uint UserId => ReadUInt32("NFSSHARP_NFS_UID");

    public static uint GroupId => ReadUInt32("NFSSHARP_NFS_GID");

    public static int PortmapPort => ReadInt32("NFSSHARP_NFS_PORTMAP_PORT", 111);

    private static bool UsesDefaultExportEndpoint =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NFSSHARP_NFS_SERVER")) &&
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NFSSHARP_NFS_EXPORT"));

    private static uint ReadUInt32(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        return uint.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be an unsigned integer.");
    }

    private static int ReadInt32(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return int.TryParse(value, out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : throw new InvalidOperationException($"{name} must be a TCP port number.");
    }
}
