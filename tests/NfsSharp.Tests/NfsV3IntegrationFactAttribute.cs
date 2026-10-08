namespace NfsSharp.Tests;

/// <summary>
/// Marks a test as an opt-in NFSv3 integration fact: skipped unless NFSSHARP_RUN_NFSV3_INTEGRATION=1
/// is set in the environment (see NfsV3IntegrationEnvironment).
/// </summary>
public sealed class NfsV3IntegrationFactAttribute : FactAttribute
{
    public NfsV3IntegrationFactAttribute()
    {
        if (!NfsV3IntegrationEnvironment.IsEnabled)
            Skip = "Set NFSSHARP_RUN_NFSV3_INTEGRATION=1 to run real-server NFSv3 tests.";
    }
}

/// <summary>
/// Reads the NFSv3 integration environment (NFSSHARP_NFS_*): server, export path, uid/gid,
/// portmap port, expected export group, and optional server-behavior expectations.
/// </summary>
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

    public static NfsV3ReplacementRenameOutcome ExpectedReplacementRenameOutcome
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(
                "NFSSHARP_NFS_EXPECTED_REPLACEMENT_RENAME_OUTCOME");
            if (string.IsNullOrWhiteSpace(value))
                return NfsV3ReplacementRenameOutcome.Unspecified;

            return value.Trim().ToLowerInvariant() switch
            {
                "replace-target" => NfsV3ReplacementRenameOutcome.ReplaceTarget,
                "io-preserves-both" => NfsV3ReplacementRenameOutcome.IoPreservesBoth,
                _ => throw new InvalidOperationException(
                    "NFSSHARP_NFS_EXPECTED_REPLACEMENT_RENAME_OUTCOME must be " +
                    "'replace-target' or 'io-preserves-both'.")
            };
        }
    }

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

/// <summary>
/// Server-dependent RENAME-over-existing-target behavior: either the target is replaced,
/// or both names survive (POSIX I/O-only semantics observed on some servers).
/// </summary>
internal enum NfsV3ReplacementRenameOutcome
{
    Unspecified,
    ReplaceTarget,
    IoPreservesBoth
}
