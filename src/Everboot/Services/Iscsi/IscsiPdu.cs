using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Everboot.Services.Iscsi;

/// <summary>
/// One iSCSI PDU: a 48-byte Basic Header Segment plus an optional data
/// segment (padded to a 4-byte boundary on the wire). We negotiate header
/// and data digests off, so no digest fields are read or written.
/// </summary>
internal sealed class IscsiPdu
{
    public const int BhsLength = 48;
    public byte[] Bhs { get; } = new byte[BhsLength];
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public IscsiOpcode Opcode
    {
        get => (IscsiOpcode)(Bhs[0] & 0x3F);
        set => Bhs[0] = (byte)((Bhs[0] & 0xC0) | ((byte)value & 0x3F));
    }

    public bool ImmediateBit
    {
        get => (Bhs[0] & 0x40) != 0;
        set => Bhs[0] = (byte)(value ? (Bhs[0] | 0x40) : (Bhs[0] & ~0x40));
    }

    public byte FlagsByte
    {
        get => Bhs[1];
        set => Bhs[1] = value;
    }

    public bool FinalBit
    {
        get => (Bhs[1] & 0x80) != 0;
        set => Bhs[1] = (byte)(value ? (Bhs[1] | 0x80) : (Bhs[1] & ~0x80));
    }

    public uint InitiatorTaskTag
    {
        get => BinaryPrimitives.ReadUInt32BigEndian(Bhs.AsSpan(16, 4));
        set => BinaryPrimitives.WriteUInt32BigEndian(Bhs.AsSpan(16, 4), value);
    }

    public uint DataSegmentLength
    {
        get => (uint)((Bhs[5] << 16) | (Bhs[6] << 8) | Bhs[7]);
        set
        {
            Bhs[5] = (byte)((value >> 16) & 0xFF);
            Bhs[6] = (byte)((value >> 8) & 0xFF);
            Bhs[7] = (byte)(value & 0xFF);
        }
    }

    public byte TotalAhsLength
    {
        get => Bhs[4];
        set => Bhs[4] = value;
    }

    public ReadOnlySpan<byte> Lun => Bhs.AsSpan(8, 8);
    public void SetLun(ulong lun) => BinaryPrimitives.WriteUInt64BigEndian(Bhs.AsSpan(8, 8), lun);

    public uint StatSN
    {
        get => BinaryPrimitives.ReadUInt32BigEndian(Bhs.AsSpan(24, 4));
        set => BinaryPrimitives.WriteUInt32BigEndian(Bhs.AsSpan(24, 4), value);
    }

    public uint ExpCmdSN
    {
        get => BinaryPrimitives.ReadUInt32BigEndian(Bhs.AsSpan(28, 4));
        set => BinaryPrimitives.WriteUInt32BigEndian(Bhs.AsSpan(28, 4), value);
    }

    public uint MaxCmdSN
    {
        get => BinaryPrimitives.ReadUInt32BigEndian(Bhs.AsSpan(32, 4));
        set => BinaryPrimitives.WriteUInt32BigEndian(Bhs.AsSpan(32, 4), value);
    }

    public static async Task<IscsiPdu?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var pdu = new IscsiPdu();

        if (!await ReadExactAsync(stream, pdu.Bhs, ct).ConfigureAwait(false))
        {
            return null;
        }

        var totalAhs = pdu.TotalAhsLength;
        if (totalAhs > 0)
        {
            // We never expect AHS - read & discard to stay on PDU boundary.
            var ahsBytes = totalAhs * 4;
            var scratch = new byte[ahsBytes];
            if (!await ReadExactAsync(stream, scratch, ct).ConfigureAwait(false))
            {
                return null;
            }
        }

        var dataLen = (int)pdu.DataSegmentLength;
        if (dataLen > 0)
        {
            pdu.Data = new byte[dataLen];
            if (!await ReadExactAsync(stream, pdu.Data, ct).ConfigureAwait(false))
            {
                return null;
            }
            var pad = (4 - (dataLen & 3)) & 3;
            if (pad > 0)
            {
                var scratch = new byte[pad];
                if (!await ReadExactAsync(stream, scratch, ct).ConfigureAwait(false))
                {
                    return null;
                }
            }
        }
        return pdu;
    }

    public async Task WriteAsync(NetworkStream stream, CancellationToken ct)
    {
        DataSegmentLength = (uint)Data.Length;
        await stream.WriteAsync(Bhs, ct).ConfigureAwait(false);
        if (Data.Length > 0)
        {
            await stream.WriteAsync(Data, ct).ConfigureAwait(false);
            var pad = (4 - (Data.Length & 3)) & 3;
            if (pad > 0)
            {
                var padding = new byte[pad];
                await stream.WriteAsync(padding, ct).ConfigureAwait(false);
            }
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }
}
