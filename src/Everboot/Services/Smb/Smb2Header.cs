using System;
using System.Buffers.Binary;

namespace Everboot.Services.Smb;

/// <summary>
/// 64-byte SMB2 synchronous header (MS-SMB2 §2.2.1.2). Entirely little-endian.
/// We don't support the async / signed / encrypted variants - this is a tiny
/// read-only target that negotiates everything off.
/// </summary>
internal sealed class Smb2Header
{
    public const int HeaderLength = 64;

    public byte[] Buffer { get; } = new byte[HeaderLength];

    public uint Status
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(8, 4));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(8, 4), value);
    }

    public Smb2Command Command
    {
        get => (Smb2Command)BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(12, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(12, 2), (ushort)value);
    }

    public ushort CreditCharge
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(6, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(6, 2), value);
    }

    public ushort CreditRequest
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(14, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(14, 2), value);
    }

    public uint Flags
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(16, 4));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(16, 4), value);
    }

    public uint NextCommand
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(20, 4));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(20, 4), value);
    }

    public ulong MessageId
    {
        get => BinaryPrimitives.ReadUInt64LittleEndian(Buffer.AsSpan(24, 8));
        set => BinaryPrimitives.WriteUInt64LittleEndian(Buffer.AsSpan(24, 8), value);
    }

    public uint TreeId
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(36, 4));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(36, 4), value);
    }

    public ulong SessionId
    {
        get => BinaryPrimitives.ReadUInt64LittleEndian(Buffer.AsSpan(40, 8));
        set => BinaryPrimitives.WriteUInt64LittleEndian(Buffer.AsSpan(40, 8), value);
    }

    public static Smb2Header Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            throw new InvalidOperationException("SMB2 header truncated");
        }
        if (data[0] != 0xFE || data[1] != (byte)'S' || data[2] != (byte)'M' || data[3] != (byte)'B')
        {
            throw new InvalidOperationException("not an SMB2 packet");
        }
        var h = new Smb2Header();
        data[..HeaderLength].CopyTo(h.Buffer);
        return h;
    }

    /// <summary>
    /// Build a response header by mirroring the inbound request and flipping
    /// the SERVER_TO_REDIR (response) flag.
    /// </summary>
    public Smb2Header BuildResponse(uint status = NtStatus.Success)
    {
        var response = new Smb2Header();
        response.Buffer[0] = 0xFE;
        response.Buffer[1] = (byte)'S';
        response.Buffer[2] = (byte)'M';
        response.Buffer[3] = (byte)'B';
        BinaryPrimitives.WriteUInt16LittleEndian(response.Buffer.AsSpan(4, 2), 64); // StructureSize
        response.CreditCharge = CreditCharge;
        response.Status = status;
        response.Command = Command;
        response.CreditRequest = 1; // grant 1 credit per response - good enough
        response.Flags = Smb2HeaderFlags.ServerToRedir;
        response.MessageId = MessageId;
        response.TreeId = TreeId;
        response.SessionId = SessionId;
        return response;
    }
}
