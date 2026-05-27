// Minimal iSCSI initiator simulator: logs in to a target, issues Inquiry +
// ReadCapacity + a small Read, prints the bytes, logs out. Used to smoke-test
// Everboot's iSCSI implementation without needing real PXE hardware or
// Windows iSCSI initiator.
//
// usage: dotnet run --project tools/IscsiProbe -- <host> <port> <target-iqn>

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: IscsiProbe <host> <port> <target-iqn>");
    return 1;
}

var host = args[0];
var port = int.Parse(args[1]);
var target = args[2];

using var client = new TcpClient();
await client.ConnectAsync(host, port);
client.NoDelay = true;
using var stream = client.GetStream();

Console.WriteLine($"connected to {host}:{port}");

// -------- Login --------
{
    var keys = new (string, string)[]
    {
        ("InitiatorName", "iqn.2010-04.org.example:initiator"),
        ("SessionType",   "Normal"),
        ("TargetName",    target),
        ("HeaderDigest",  "None"),
        ("DataDigest",    "None"),
        ("MaxRecvDataSegmentLength", "65536"),
        ("DefaultTime2Wait", "0"),
        ("DefaultTime2Retain", "0"),
        ("InitialR2T",    "Yes"),
        ("ImmediateData", "Yes"),
        ("ErrorRecoveryLevel", "0"),
    };
    var data = BuildText(keys);
    var bhs = new byte[48];
    bhs[0] = 0x03 | 0x40;   // Login Request, Immediate
    bhs[1] = 0x80 | (1 << 2) | 3; // T + CSG=Operational + NSG=FullFeature
    WriteDataLen(bhs, data.Length);
    Random.Shared.NextBytes(bhs.AsSpan(8, 6)); // ISID
    // bytes 14-15 TSIH = 0
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(16, 4), 0xdeadbeef);
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28, 4), 0); // CmdSN
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(32, 4), 0); // ExpStatSN

    await SendPdu(stream, bhs, data);
    var (respBhs, _) = await ReadPdu(stream);
    Console.WriteLine($"login response opcode=0x{respBhs[0] & 0x3F:X2} status=0x{respBhs[36]:X2}{respBhs[37]:X2}");
    if ((respBhs[36] | respBhs[37]) != 0)
    {
        Console.Error.WriteLine("login failed");
        return 2;
    }
}

uint cmdSn = 1;
uint expStatSn = 1;

// -------- Inquiry --------
{
    var cdb = new byte[16];
    cdb[0] = 0x12; // Inquiry
    BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(3, 2), 36); // allocation length
    var (respBhs, respData) = await IssueScsiCommand(stream, cdb, 36, cmdSn++, expStatSn);
    expStatSn++;
    Console.WriteLine($"INQUIRY ({respData.Length} bytes): type=0x{respData[0]:X2} vendor='{Encoding.ASCII.GetString(respData, 8, 8).Trim()}' product='{Encoding.ASCII.GetString(respData, 16, 16).Trim()}'");
}

// -------- ReadCapacity(10) --------
long lastLba;
int blockSize;
{
    var cdb = new byte[16];
    cdb[0] = 0x25;
    var (respBhs, respData) = await IssueScsiCommand(stream, cdb, 8, cmdSn++, expStatSn);
    expStatSn++;
    lastLba = BinaryPrimitives.ReadUInt32BigEndian(respData.AsSpan(0, 4));
    blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(respData.AsSpan(4, 4));
    Console.WriteLine($"READ_CAPACITY: last_lba={lastLba}, block_size={blockSize}, total={(lastLba + 1L) * blockSize:N0} bytes");
}

// -------- Read first 4 blocks --------
{
    var cdb = new byte[16];
    cdb[0] = 0x28; // Read(10)
    BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), 0);   // LBA 0
    BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), 4);  // 4 blocks
    var (respBhs, respData) = await IssueScsiCommand(stream, cdb, 4 * blockSize, cmdSn++, expStatSn);
    expStatSn++;
    Console.WriteLine($"READ(10) lba=0 blocks=4 -> {respData.Length} bytes");
    Console.WriteLine($"first 32 bytes: {BitConverter.ToString(respData.AsSpan(0, Math.Min(32, respData.Length)).ToArray())}");
}

// -------- Logout --------
{
    var bhs = new byte[48];
    bhs[0] = 0x06 | 0x40;
    bhs[1] = 0x80;            // F bit + reason 0 (close session)
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(16, 4), 0xcafef00d);
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28, 4), cmdSn);
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(32, 4), expStatSn);
    await SendPdu(stream, bhs, Array.Empty<byte>());
    var (_, _) = await ReadPdu(stream);
    Console.WriteLine("logout ok");
}

return 0;

// ---------- helpers ----------

static byte[] BuildText((string K, string V)[] keys)
{
    using var ms = new MemoryStream();
    foreach (var (k, v) in keys)
    {
        var bytes = Encoding.ASCII.GetBytes($"{k}={v}");
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0);
    }
    return ms.ToArray();
}

static void WriteDataLen(byte[] bhs, int len)
{
    bhs[5] = (byte)((len >> 16) & 0xFF);
    bhs[6] = (byte)((len >> 8) & 0xFF);
    bhs[7] = (byte)(len & 0xFF);
}

static async Task SendPdu(NetworkStream s, byte[] bhs, byte[] data)
{
    WriteDataLen(bhs, data.Length);
    await s.WriteAsync(bhs);
    if (data.Length > 0)
    {
        await s.WriteAsync(data);
        var pad = (4 - (data.Length & 3)) & 3;
        if (pad > 0)
        {
            await s.WriteAsync(new byte[pad]);
        }
    }
    await s.FlushAsync();
}

static async Task<(byte[] Bhs, byte[] Data)> ReadPdu(NetworkStream s)
{
    var bhs = new byte[48];
    await ReadExact(s, bhs);
    var dataLen = (bhs[5] << 16) | (bhs[6] << 8) | bhs[7];
    var data = new byte[dataLen];
    if (dataLen > 0)
    {
        await ReadExact(s, data);
        var pad = (4 - (dataLen & 3)) & 3;
        if (pad > 0)
        {
            var scratch = new byte[pad];
            await ReadExact(s, scratch);
        }
    }
    return (bhs, data);
}

static async Task ReadExact(Stream s, byte[] buf)
{
    var off = 0;
    while (off < buf.Length)
    {
        var n = await s.ReadAsync(buf.AsMemory(off));
        if (n == 0) throw new EndOfStreamException();
        off += n;
    }
}

static async Task<(byte[] Bhs, byte[] Data)> IssueScsiCommand(NetworkStream s, byte[] cdb, int expectedLen, uint cmdSn, uint expStatSn)
{
    var bhs = new byte[48];
    bhs[0] = 0x01 | 0x40; // SCSI Command + Immediate
    // F + R bits: F=final (last PDU), R=data read direction
    bhs[1] = 0x80 | 0x40;
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(16, 4), (uint)(0xa0000000 | cmdSn)); // ITT
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(20, 4), (uint)expectedLen);
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(24, 4), cmdSn);
    BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28, 4), expStatSn);
    cdb.CopyTo(bhs.AsSpan(32, 16));

    await SendPdu(s, bhs, Array.Empty<byte>());

    // Collect Data-In PDUs until we see the F+S bits (status piggyback)
    // or a separate SCSI Response PDU. For simplicity, read one PDU and
    // assume our small commands fit in a single Data-In with F+S.
    var assembled = new MemoryStream();
    while (true)
    {
        var (respBhs, respData) = await ReadPdu(s);
        var op = respBhs[0] & 0x3F;
        if (op == 0x25)
        {
            assembled.Write(respData);
            if ((respBhs[1] & 0x80) != 0) // F bit
            {
                return (respBhs, assembled.ToArray());
            }
        }
        else if (op == 0x21)
        {
            // SCSI Response (status only, no data)
            return (respBhs, assembled.ToArray());
        }
        else
        {
            throw new InvalidOperationException($"unexpected opcode 0x{op:X2}");
        }
    }
}
