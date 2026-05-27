using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Everboot.Services;

internal enum TftpOpcode : ushort
{
    ReadRequest = 1,
    WriteRequest = 2,
    Data = 3,
    Acknowledgment = 4,
    Error = 5,
    OptionAcknowledgment = 6,
}

internal enum TftpErrorCode : ushort
{
    NotDefined = 0,
    FileNotFound = 1,
    AccessViolation = 2,
    DiskFull = 3,
    IllegalOperation = 4,
    UnknownTransferId = 5,
    FileExists = 6,
    NoSuchUser = 7,
    InvalidOption = 8,
}

internal readonly record struct TftpRequest(
    string Filename,
    string Mode,
    IReadOnlyList<KeyValuePair<string, string>> Options);

internal static class TftpProtocol
{
    /// <summary>
    /// Parses an RRQ / WRQ payload (the bytes AFTER the 2-byte opcode).
    /// Layout: filename \0 mode \0 [opt \0 val \0]*
    /// </summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> payload, out TftpRequest request)
    {
        request = default;

        var fields = new List<string>(8);
        var start = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != 0)
            {
                continue;
            }
            fields.Add(Encoding.ASCII.GetString(payload[start..i]));
            start = i + 1;
        }

        if (fields.Count < 2)
        {
            return false;
        }

        var options = new List<KeyValuePair<string, string>>();
        for (var i = 2; i + 1 < fields.Count; i += 2)
        {
            options.Add(new KeyValuePair<string, string>(fields[i], fields[i + 1]));
        }

        request = new TftpRequest(fields[0], fields[1], options);
        return true;
    }

    public static byte[] BuildError(TftpErrorCode code, string message)
    {
        var messageBytes = Encoding.ASCII.GetBytes(message);
        var buffer = new byte[4 + messageBytes.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), (ushort)TftpOpcode.Error);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)code);
        messageBytes.CopyTo(buffer.AsSpan(4));
        buffer[^1] = 0;
        return buffer;
    }

    public static byte[] BuildOack(IEnumerable<KeyValuePair<string, string>> options)
    {
        using var ms = new MemoryStream(64);
        Span<byte> header = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)TftpOpcode.OptionAcknowledgment);
        ms.Write(header);
        foreach (var (key, value) in options)
        {
            ms.Write(Encoding.ASCII.GetBytes(key));
            ms.WriteByte(0);
            ms.Write(Encoding.ASCII.GetBytes(value));
            ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    public static int WriteDataHeader(Span<byte> buffer, ushort blockNumber)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], (ushort)TftpOpcode.Data);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), blockNumber);
        return 4;
    }

    public static bool TryReadAck(ReadOnlySpan<byte> packet, out ushort blockNumber)
    {
        blockNumber = 0;
        if (packet.Length < 4)
        {
            return false;
        }
        var op = (TftpOpcode)BinaryPrimitives.ReadUInt16BigEndian(packet[..2]);
        if (op != TftpOpcode.Acknowledgment)
        {
            return false;
        }
        blockNumber = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        return true;
    }

    public static bool IsError(ReadOnlySpan<byte> packet, out TftpErrorCode code, out string message)
    {
        code = TftpErrorCode.NotDefined;
        message = string.Empty;
        if (packet.Length < 4)
        {
            return false;
        }
        if ((TftpOpcode)BinaryPrimitives.ReadUInt16BigEndian(packet[..2]) != TftpOpcode.Error)
        {
            return false;
        }
        code = (TftpErrorCode)BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        var msgBytes = packet[4..];
        var zero = msgBytes.IndexOf((byte)0);
        if (zero >= 0)
        {
            msgBytes = msgBytes[..zero];
        }
        message = Encoding.ASCII.GetString(msgBytes);
        return true;
    }
}
