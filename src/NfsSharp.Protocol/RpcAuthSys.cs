namespace NfsSharp.Protocol;

/// <summary>Encodes ONC RPC AUTH_SYS credentials.</summary>
public static class RpcAuthSys
{
    /// <summary>Maximum AUTH_SYS machine-name length in UTF-8 bytes.</summary>
    public const int MaxMachineNameLength = 255;

    /// <summary>Maximum number of AUTH_SYS auxiliary groups.</summary>
    public const int MaxAuxiliaryGroups = 16;

    /// <summary>
    /// Encodes an AUTH_SYS credential body (RFC 5531 field order: stamp, machine name, uid, gid, gids).
    /// User and group identifiers are encoded as unsigned 32-bit values;
    /// machine names longer than 255 UTF-8 bytes are truncated on a character boundary.
    /// </summary>
    public static byte[] Encode(uint stamp, string machineName, uint userId, uint groupId, IReadOnlyList<uint> auxiliaryGroups)
    {
        ArgumentNullException.ThrowIfNull(machineName);
        ArgumentNullException.ThrowIfNull(auxiliaryGroups);
        // The wire format hard-caps gids[] at MaxAuxiliaryGroups; reject before encoding.
        if (auxiliaryGroups.Count > MaxAuxiliaryGroups)
            throw new NfsException($"AUTH_SYS supports at most {MaxAuxiliaryGroups} auxiliary groups.");

        var machineNameBytes = System.Text.Encoding.UTF8.GetBytes(machineName);
        // Cap the opaque length first; the loop below then trims to a character boundary.
        var machineNameLength = Math.Min(machineNameBytes.Length, MaxMachineNameLength);
        // Back up off UTF-8 continuation bytes (0b10xxxxxx) so truncation never splits a character.
        while (machineNameLength > 0 && machineNameLength < machineNameBytes.Length &&
               (machineNameBytes[machineNameLength] & 0xC0) == 0x80)
        {
            machineNameLength--;
        }

        // RFC 5531 body order: stamp, machine name, uid, gid, then the gids array with its count.
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
