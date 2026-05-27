using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Everboot.Services;

/// <summary>
/// RFC 2131 DHCP packet. Fixed 236-byte BOOTP header + 4-byte magic cookie
/// (0x63 0x82 0x53 0x63) + variable TLV options terminated by option 255.
/// Parsing is lenient (used for inbound from arbitrary clients); serialization
/// pads to 300 bytes to keep cranky old firmware happy.
/// </summary>
internal sealed class DhcpPacket
{
    public const int MinimumWireSize = 240;
    private const int SerializedMinSize = 300;
    private static readonly byte[] MagicCookie = { 0x63, 0x82, 0x53, 0x63 };

    public DhcpOp Op { get; set; } = DhcpOp.BootReply;
    public byte HType { get; set; } = 1; // ethernet
    public byte HLen { get; set; } = 6;  // MAC length
    public byte Hops { get; set; }
    public uint Xid { get; set; }
    public ushort Secs { get; set; }
    public ushort Flags { get; set; }
    public IPAddress Ciaddr { get; set; } = IPAddress.Any;
    public IPAddress Yiaddr { get; set; } = IPAddress.Any;
    public IPAddress Siaddr { get; set; } = IPAddress.Any;
    public IPAddress Giaddr { get; set; } = IPAddress.Any;
    public byte[] Chaddr { get; set; } = new byte[16];
    public string Sname { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public Dictionary<byte, byte[]> Options { get; } = new();

    public bool BroadcastRequested => (Flags & 0x8000) != 0;

    public DhcpMessageType? MessageType =>
        Options.TryGetValue(DhcpOption.MessageType, out var v) && v.Length >= 1
            ? (DhcpMessageType)v[0]
            : null;

    public string? VendorClassIdentifier =>
        Options.TryGetValue(DhcpOption.VendorClassIdentifier, out var v)
            ? Encoding.ASCII.GetString(v)
            : null;

    public string? UserClass =>
        Options.TryGetValue(DhcpOption.UserClass, out var v)
            ? Encoding.ASCII.GetString(v)
            : null;

    public ushort? ClientArchitecture =>
        Options.TryGetValue(DhcpOption.ClientArchitecture, out var v) && v.Length >= 2
            ? BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(0, 2))
            : null;

    public string MacAddress
    {
        get
        {
            var len = Math.Min((int)HLen, Chaddr.Length);
            var sb = new StringBuilder(len * 3);
            for (var i = 0; i < len; i++)
            {
                if (i > 0)
                {
                    sb.Append(':');
                }
                sb.Append(Chaddr[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out DhcpPacket packet)
    {
        packet = new DhcpPacket();
        if (data.Length < MinimumWireSize)
        {
            return false;
        }

        packet.Op = (DhcpOp)data[0];
        packet.HType = data[1];
        packet.HLen = data[2];
        packet.Hops = data[3];
        packet.Xid = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        packet.Secs = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        packet.Flags = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10, 2));
        packet.Ciaddr = new IPAddress(data.Slice(12, 4).ToArray());
        packet.Yiaddr = new IPAddress(data.Slice(16, 4).ToArray());
        packet.Siaddr = new IPAddress(data.Slice(20, 4).ToArray());
        packet.Giaddr = new IPAddress(data.Slice(24, 4).ToArray());
        packet.Chaddr = data.Slice(28, 16).ToArray();
        packet.Sname = ReadNullTerminated(data.Slice(44, 64));
        packet.File = ReadNullTerminated(data.Slice(108, 128));

        if (!data.Slice(236, 4).SequenceEqual(MagicCookie))
        {
            return false;
        }

        var options = data[240..];
        var i = 0;
        while (i < options.Length)
        {
            var tag = options[i++];
            if (tag == DhcpOption.End)
            {
                break;
            }
            if (tag == DhcpOption.Pad)
            {
                continue;
            }
            if (i >= options.Length)
            {
                return false;
            }
            int len = options[i++];
            if (i + len > options.Length)
            {
                return false;
            }
            packet.Options[tag] = options.Slice(i, len).ToArray();
            i += len;
        }

        return true;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream(SerializedMinSize);

        ms.WriteByte((byte)Op);
        ms.WriteByte(HType);
        ms.WriteByte(HLen);
        ms.WriteByte(Hops);

        Span<byte> scratch = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(scratch, Xid);
        ms.Write(scratch);

        BinaryPrimitives.WriteUInt16BigEndian(scratch[..2], Secs);
        ms.Write(scratch[..2]);

        BinaryPrimitives.WriteUInt16BigEndian(scratch[..2], Flags);
        ms.Write(scratch[..2]);

        ms.Write(Ciaddr.GetAddressBytes());
        ms.Write(Yiaddr.GetAddressBytes());
        ms.Write(Siaddr.GetAddressBytes());
        ms.Write(Giaddr.GetAddressBytes());

        var chaddr = new byte[16];
        Array.Copy(Chaddr, chaddr, Math.Min(Chaddr.Length, 16));
        ms.Write(chaddr);

        WriteFixedString(ms, Sname, 64);
        WriteFixedString(ms, File, 128);

        ms.Write(MagicCookie);

        // MessageType must come first per RFC 2131 strong recommendation.
        if (Options.TryGetValue(DhcpOption.MessageType, out var mt))
        {
            WriteOption(ms, DhcpOption.MessageType, mt);
        }
        foreach (var (tag, value) in Options)
        {
            if (tag == DhcpOption.MessageType)
            {
                continue;
            }
            WriteOption(ms, tag, value);
        }
        ms.WriteByte(DhcpOption.End);

        while (ms.Length < SerializedMinSize)
        {
            ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    public void SetOption(byte tag, byte value) => Options[tag] = new[] { value };
    public void SetOption(byte tag, byte[] value) => Options[tag] = value;
    public void SetOption(byte tag, string value) => Options[tag] = Encoding.ASCII.GetBytes(value);
    public void SetOption(byte tag, IPAddress value) => Options[tag] = value.GetAddressBytes();

    private static void WriteOption(MemoryStream ms, byte tag, byte[] value)
    {
        if (value.Length > 255)
        {
            throw new InvalidOperationException($"DHCP option {tag} exceeds 255 bytes");
        }
        ms.WriteByte(tag);
        ms.WriteByte((byte)value.Length);
        ms.Write(value);
    }

    private static void WriteFixedString(MemoryStream ms, string value, int length)
    {
        var bytes = new byte[length];
        if (!string.IsNullOrEmpty(value))
        {
            var src = Encoding.ASCII.GetBytes(value);
            Array.Copy(src, bytes, Math.Min(src.Length, length - 1)); // leave a terminator
        }
        ms.Write(bytes);
    }

    private static string ReadNullTerminated(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }
        return end == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..end]);
    }
}
