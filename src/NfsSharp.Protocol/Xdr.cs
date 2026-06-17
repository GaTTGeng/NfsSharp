using System.Buffers.Binary;
using System.Text;

namespace NfsSharp.Protocol;

public sealed class XdrWriter
{
    private readonly MemoryStream _stream = new();

    public void Bool(bool value) => UInt(value ? 1u : 0u);

    public void UInt(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void ULong(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void Raw(ReadOnlySpan<byte> data) => _stream.Write(data);

    public void FixedBytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        Pad(data.Length);
    }

    public void Opaque(ReadOnlySpan<byte> data)
    {
        UInt((uint)data.Length);
        _stream.Write(data);
        Pad(data.Length);
    }

    public void Str(string value) => Opaque(Encoding.UTF8.GetBytes(value));

    public byte[] ToArray() => _stream.ToArray();

    private void Pad(int length)
    {
        var pad = (4 - (length & 3)) & 3;
        for (var i = 0; i < pad; i++)
            _stream.WriteByte(0);
    }
}

public sealed class XdrReader
{
    private const int MaxOpaqueLength = 64 * 1024 * 1024;
    private readonly byte[] _buffer;
    private int _position;

    public XdrReader(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public int Remaining => _buffer.Length - _position;

    public uint UInt()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public ulong ULong()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    public bool Bool() => UInt() != 0;

    public byte[] Opaque()
    {
        var length = CheckedLength(UInt());
        Ensure(length);
        var data = _buffer.AsSpan(_position, length).ToArray();
        _position += length;
        SkipPad(length);
        return data;
    }

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

    public string Str() => Encoding.UTF8.GetString(Opaque());

    public void SkipOpaque()
    {
        var length = CheckedLength(UInt());
        Ensure(length);
        _position += length;
        SkipPad(length);
    }

    private static int CheckedLength(uint value)
    {
        if (value > MaxOpaqueLength)
            throw new NfsException($"XDR opaque length is too large: {value}.");

        return (int)value;
    }

    private void SkipPad(int length)
    {
        var pad = (4 - (length & 3)) & 3;
        Ensure(pad);
        _position += pad;
    }

    private void Ensure(int count)
    {
        if (count < 0 || count > Remaining)
            throw new NfsException($"Malformed XDR payload. Need {count} bytes, only {Remaining} left.");
    }
}
