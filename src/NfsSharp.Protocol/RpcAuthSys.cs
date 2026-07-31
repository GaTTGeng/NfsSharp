namespace NfsSharp.Protocol;

/// <summary>Encodes ONC RPC AUTH_SYS credentials.</summary>
public static class RpcAuthSys
{
    /// <summary>Maximum AUTH_SYS machine-name length in UTF-8 bytes.</summary>
    public const int MaxMachineNameLength = 255;

    /// <summary>Maximum number of AUTH_SYS auxiliary groups.</summary>
    public const int MaxAuxiliaryGroups = 16;

    /// <summary>
    /// Encodes an AUTH_SYS credential body. User and group identifiers are encoded as unsigned 32-bit values;
    /// machine names longer than 255 UTF-8 bytes are truncated on a character boundary.
    /// </summary>
    public static byte[] Encode(uint stamp, string machineName, uint userId, uint groupId, IReadOnlyList<uint> auxiliaryGroups)
    {
        ArgumentNullException.ThrowIfNull(machineName);
        ArgumentNullException.ThrowIfNull(auxiliaryGroups);
        if (auxiliaryGroups.Count > MaxAuxiliaryGroups)
            throw new NfsException($"AUTH_SYS supports at most {MaxAuxiliaryGroups} auxiliary groups.");

        var machineNameBytes = System.Text.Encoding.UTF8.GetBytes(machineName);
        var machineNameLength = Math.Min(machineNameBytes.Length, MaxMachineNameLength);
        while (machineNameLength > 0 && machineNameLength < machineNameBytes.Length &&
               (machineNameBytes[machineNameLength] & 0xC0) == 0x80)
        {
            machineNameLength--;
        }

        var writer = new XdrWriter();
        writer.UInt(stamp);
        writer.Opaque(machineNameBytes.AsSpan(0, machineNameLength));
        writer.UInt(userId);
        writer.UInt(groupId);
        writer.UInt((uint)auxiliaryGroups.Count);
        foreach (var group in auxiliaryGroups)
            writer.UInt(group);
        return writer.ToArray();
    }
}
