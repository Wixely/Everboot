using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Everboot.Services.Smb;

// Wire compatibility for an explicitly public, guest-only share. This does not
// validate identities or passwords, and never retains/logs authentication data.
internal static class SmbGuestSecurity
{
    private static readonly byte[] NtlmOid = Convert.FromHexString("060A2B06010401823702020A");
    public static byte[] NegotiateToken => Der(0x60,
        Convert.FromHexString("06062B0601050502").Concat(Der(0xa0, Der(0x30, Der(0xa0, Der(0x30, NtlmOid))))).ToArray());

    public static uint MessageType(ReadOnlySpan<byte> token)
    {
        var at = token.IndexOf("NTLMSSP\0"u8);
        return at >= 0 && token.Length - at >= 12
            ? BinaryPrimitives.ReadUInt32LittleEndian(token.Slice(at + 8, 4)) : 0;
    }

    public static byte[] Challenge(uint clientFlags = 0xe2088297, bool raw = false)
    {
        var name = Encoding.Unicode.GetBytes("EVERBOOT");
        using var infoStream = new MemoryStream();
        using (var writer = new BinaryWriter(infoStream, Encoding.Unicode, true))
        {
            foreach (var pair in new[] { (Id: 2, Name: "WORKGROUP"), (Id: 1, Name: "EVERBOOT"), (Id: 4, Name: "workgroup"), (Id: 3, Name: "everboot") })
            {
                var value = Encoding.Unicode.GetBytes(pair.Name);
                writer.Write((ushort)pair.Id); writer.Write((ushort)value.Length); writer.Write(value);
            }
            writer.Write((ushort)7); writer.Write((ushort)8); writer.Write(DateTime.UtcNow.ToFileTimeUtc());
            writer.Write(0);
        }
        var info = infoStream.ToArray();
        var message = new byte[56 + name.Length + info.Length];
        "NTLMSSP\0"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 2);
        Field(message.AsSpan(12), name.Length, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), 0x00828204 | (clientFlags & 0xe2080031));
        RandomNumberGenerator.Fill(message.AsSpan(24, 8));
        if ((clientFlags & 0x02000000) != 0) { message[48] = 6; message[49] = 1; message[55] = 15; }
        Field(message.AsSpan(40), info.Length, 56 + name.Length);
        name.CopyTo(message, 56); info.CopyTo(message, 56 + name.Length);
        return raw ? message : Response(1, message);
    }

    public static byte[] Complete => Response(0, null);
    private static byte[] Response(byte state, byte[]? token)
    {
        var fields = Der(0xa0, new byte[] { 0x0a, 1, state });
        if (token is not null)
            fields = fields.Concat(Der(0xa1, NtlmOid)).Concat(Der(0xa2, Der(4, token))).ToArray();
        return Der(0xa1, Der(0x30, fields));
    }
    private static void Field(Span<byte> target, int length, int offset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(target[2..], (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(target[4..], (uint)offset);
    }
    private static byte[] Der(byte tag, byte[] value)
    {
        if (value.Length > 255) throw new ArgumentOutOfRangeException(nameof(value));
        return value.Length < 128 ? new byte[] { tag, (byte)value.Length }.Concat(value).ToArray()
            : new byte[] { tag, 0x81, (byte)value.Length }.Concat(value).ToArray();
    }
}
