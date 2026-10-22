using System.Buffers.Binary;
using System.Text;

namespace Everboot.Services.Nfs;

// RFC 4506. All lengths are checked before allocation; padding is four-byte aligned.
internal sealed class XdrReader(byte[] data)
{
    private int _offset;
    public uint UInt() => BinaryPrimitives.ReadUInt32BigEndian(Fixed(4));
    public ulong ULong() => ((ulong)UInt() << 32) | UInt();
    public ReadOnlySpan<byte> Fixed(int count)
    {
        if (count < 0 || count > data.Length - _offset) throw new InvalidDataException("Truncated XDR record.");
        var result = data.AsSpan(_offset, count);
        _offset += count;
        return result;
    }
    public byte[] Opaque(int maximum)
    {
        var length = UInt();
        if (length > maximum) throw new InvalidDataException("XDR field exceeds limit.");
        var result = Fixed((int)length).ToArray();
        Fixed((4 - (int)length % 4) % 4);
        return result;
    }
    public string Text(int maximum) => new UTF8Encoding(false, true).GetString(Opaque(maximum));
}

internal sealed class XdrWriter
{
    private readonly MemoryStream _stream = new();
    public int Length => checked((int)_stream.Length);
    public void Truncate(int length) { _stream.SetLength(length); _stream.Position = length; }
    public void UInt(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }
    public void ULong(ulong value) { UInt((uint)(value >> 32)); UInt((uint)value); }
    public void Bool(bool value) => UInt(value ? 1u : 0u);
    public void Fixed(ReadOnlySpan<byte> value) => _stream.Write(value);
    public void Opaque(ReadOnlySpan<byte> value)
    {
        UInt((uint)value.Length);
        Fixed(value);
        for (var i = value.Length; i % 4 != 0; i++) _stream.WriteByte(0);
    }
    public void Text(string value) => Opaque(Encoding.UTF8.GetBytes(value));
    public byte[] ToArray() => _stream.ToArray();
}
