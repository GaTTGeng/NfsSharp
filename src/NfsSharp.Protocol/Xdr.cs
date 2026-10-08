using System.Buffers.Binary;
using System.Text;

namespace NfsSharp.Protocol;

/// <summary>Encodes big-endian XDR values (RFC 4506) for ONC RPC messages.</summary>
public sealed class XdrWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>Writes a boolean as a 32-bit value of 0 or 1.</summary>
    public void Bool(bool value) => UInt(value ? 1u : 0u);

    /// <summary>Writes a 32-bit unsigned integer in big-endian order.</summary>
    public void UInt(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    /// <summary>Writes a 64-bit unsigned integer in big-endian order.</summary>
    public void ULong(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    /// <summary>Writes raw bytes with no length prefix and no padding.</summary>
    public void Raw(ReadOnlySpan<byte> data) => _stream.Write(data);

    /// <summary>Writes fixed-size opaque bytes and pads to a 4-byte boundary.</summary>
    public void FixedBytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        Pad(data.Length);
    }

    /// <summary>Writes a length-prefixed opaque value padded to a 4-byte boundary.</summary>
    public void Opaque(ReadOnlySpan<byte> data)
    {
        UInt((uint)data.Length);
        _stream.Write(data);
        Pad(data.Length);
    }

    /// <summary>Writes a UTF-8 string as a length-prefixed opaque value.</summary>
    public void Str(string value) => Opaque(Encoding.UTF8.GetBytes(value));

    /// <summary>Returns the encoded message bytes.</summary>
    public byte[] ToArray() => _stream.ToArray();

    private void Pad(int length)
    {
        // RFC 4506: opaque data is zero-padded to the next 4-byte boundary.
        var pad = (4 - (length & 3)) & 3;
        for (var i = 0; i < pad; i++)
            _stream.WriteByte(0);
    }
}

/// <summary>Decodes big-endian XDR values (RFC 4506) from an RPC message buffer.</summary>
public sealed class XdrReader
{
    // Cap untrusted opaque length prefixes so a malformed payload cannot force huge allocations.
    private const int MaxOpaqueLength = 64 * 1024 * 1024;
    private readonly byte[] _buffer;
    private int _position;

    public XdrReader(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    /// <summary>Bytes not yet consumed from the buffer.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>Reads a big-endian 32-bit unsigned integer.</summary>
    public uint UInt()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>Reads a big-endian 64-bit unsigned integer.</summary>
    public ulong ULong()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>Reads a boolean; wire values other than 0 or 1 are rejected.</summary>
    public bool Bool()
    {
        var value = UInt();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new NfsException($"Malformed XDR boolean value: {value}.")
        };
    }

    /// <summary>Reads a length-prefixed opaque value up to the default size cap.</summary>
    public byte[] Opaque() => Opaque(MaxOpaqueLength);

    /// <summary>Reads an opaque value constrained to <paramref name="maxLength"/> bytes.</summary>
    public byte[] Opaque(int maxLength)
    {
        var length = CheckedLength(UInt(), maxLength);
        Ensure(length);
        var data = _buffer.AsSpan(_position, length).ToArray();
        _position += length;
        SkipPad(length);
        return data;
    }

    /// <summary>Reads fixed-size opaque bytes and consumes the trailing padding.</summary>
    public byte[] FixedBytes(int length)
    {
        if (length < 0)
            throw new NfsException($"Invalid XDR fixed byte length: {length}.");

        Ensure(length);
        var data = _buffer.AsSpan(_position, length).ToArray();
        _position += length;
        SkipPad(length);
        return data;
    }

    /// <summary>Reads a UTF-8 string encoded as a length-prefixed opaque value.</summary>
    public string Str() => Encoding.UTF8.GetString(Opaque());

    /// <summary>Returns all unread bytes and advances to the end of the buffer.</summary>
    public byte[] ReadRemainingBytes()
    {
        var data = _buffer.AsSpan(_position).ToArray();
        _position = _buffer.Length;
        return data;
    }

    /// <summary>Skips a length-prefixed opaque value up to the default size cap.</summary>
    public void SkipOpaque() => SkipOpaque(MaxOpaqueLength);

    /// <summary>Skips an opaque value constrained to <paramref name="maxLength"/> bytes.</summary>
    public void SkipOpaque(int maxLength)
    {
        var length = CheckedLength(UInt(), maxLength);
        Ensure(length);
        _position += length;
        SkipPad(length);
    }

    /// <summary>Validates an untrusted opaque length against a caller-supplied cap.</summary>
    private static int CheckedLength(uint value, int maxLength)
    {
        if (maxLength < 0 || maxLength > MaxOpaqueLength)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (value > maxLength)
            throw new NfsException($"XDR opaque length is too large: {value}.");

        return (int)value;
    }

    private void SkipPad(int length)
    {
        // RFC 4506 padding must be zero; a nonzero byte means the payload is malformed.
        var pad = (4 - (length & 3)) & 3;
        Ensure(pad);
        for (var i = 0; i < pad; i++)
        {
            if (_buffer[_position + i] != 0)
                throw new NfsException("Malformed XDR padding. Padding bytes must be zero.");
        }
        _position += pad;
    }

    private void Ensure(int count)
    {
        if (count < 0 || count > Remaining)
            throw new NfsException($"Malformed XDR payload. Need {count} bytes, only {Remaining} left.");
    }
}
