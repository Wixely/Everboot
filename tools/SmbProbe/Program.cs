// Minimal SMB2 client simulator: NEGOTIATE -> SESSION_SETUP -> TREE_CONNECT
// -> CREATE -> READ -> CLOSE -> LOGOFF. Verifies bytes match the source file
// without needing a real SMB client.
//
// usage: dotnet run --project tools/SmbProbe -- <host> <port> <share> <file>

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: SmbProbe <host> <port> <share> <file-inside-share>");
    return 1;
}

var host = args[0];
var port = int.Parse(args[1]);
var share = args[2];
var filename = args[3];

using var client = new TcpClient();
await client.ConnectAsync(host, port);
client.NoDelay = true;
using var stream = client.GetStream();

Console.WriteLine($"connected to {host}:{port}");

ulong sessionId = 0;
uint treeId = 0;
ulong msgId = 0;

// ---------- NEGOTIATE ----------
{
    var body = new byte[36];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 36);  // StructureSize
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), 1);   // DialectCount
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), 0);   // SecurityMode
    // dialect list at offset 36
    var negotiate = new byte[body.Length + 2];
    body.CopyTo(negotiate, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(negotiate.AsSpan(36, 2), 0x0202); // SMB 2.0.2
    var resp = await ExchangeAsync(stream, 0x0000, negotiate, msgId++, sessionId, treeId);
    Console.WriteLine($"NEGOTIATE: status=0x{resp.Status:X8} dialect=0x{BinaryPrimitives.ReadUInt16LittleEndian(resp.Body.AsSpan(4, 2)):X4}");
}

// ---------- SESSION_SETUP ----------
{
    var body = new byte[25];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 25);
    var resp = await ExchangeAsync(stream, 0x0001, body, msgId++, sessionId, treeId);
    sessionId = resp.SessionId;
    Console.WriteLine($"SESSION_SETUP: status=0x{resp.Status:X8} sessionId=0x{sessionId:X}");
}

// ---------- TREE_CONNECT ----------
{
    var unc = $"\\\\everboot\\{share}";
    var pathBytes = Encoding.Unicode.GetBytes(unc);
    var body = new byte[8 + pathBytes.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 9);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), 0);  // Flags
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)(64 + 8)); // PathOffset
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6, 2), (ushort)pathBytes.Length);
    pathBytes.CopyTo(body, 8);
    var resp = await ExchangeAsync(stream, 0x0003, body, msgId++, sessionId, treeId);
    treeId = resp.TreeId;
    Console.WriteLine($"TREE_CONNECT '{unc}': status=0x{resp.Status:X8} treeId=0x{treeId:X}");
}

// ---------- CREATE ----------
ulong fileId;
long fileLen;
{
    var nameBytes = Encoding.Unicode.GetBytes(filename.Replace('/', '\\'));
    var body = new byte[57 + nameBytes.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 57);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24, 4), 0x00120089); // DesiredAccess: FILE_READ_DATA etc.
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32, 4), 1);          // ShareAccess = read
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36, 4), 1);          // CreateDisposition = open
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(44, 2), (ushort)(64 + 56)); // NameOffset
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(46, 2), (ushort)nameBytes.Length);
    nameBytes.CopyTo(body, 56);
    var resp = await ExchangeAsync(stream, 0x0005, body, msgId++, sessionId, treeId);
    fileLen = BinaryPrimitives.ReadInt64LittleEndian(resp.Body.AsSpan(48, 8));
    var pid = BinaryPrimitives.ReadUInt64LittleEndian(resp.Body.AsSpan(64, 8));
    var vid = BinaryPrimitives.ReadUInt64LittleEndian(resp.Body.AsSpan(72, 8));
    fileId = vid;
    Console.WriteLine($"CREATE '{filename}': status=0x{resp.Status:X8} length={fileLen} fileId=({pid:X},{vid:X})");
}

// ---------- READ first 64 bytes ----------
{
    var body = new byte[49];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 49);
    body[2] = 80;  // Padding (byte for alignment to 8)
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), 64);  // Length
    BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8, 8), 0);    // Offset
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16, 8), fileId); // PersistentFileId
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(24, 8), fileId); // VolatileFileId
    var resp = await ExchangeAsync(stream, 0x0008, body, msgId++, sessionId, treeId);
    var dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp.Body.AsSpan(4, 4));
    var data = resp.Body.AsSpan(16, dataLen).ToArray();
    Console.WriteLine($"READ 64 bytes: status=0x{resp.Status:X8} got={data.Length}");
    Console.WriteLine($"first 32 bytes: {BitConverter.ToString(data, 0, Math.Min(32, data.Length))}");
}

// ---------- CLOSE ----------
{
    var body = new byte[24];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 24);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8, 8), fileId);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16, 8), fileId);
    var resp = await ExchangeAsync(stream, 0x0006, body, msgId++, sessionId, treeId);
    Console.WriteLine($"CLOSE: status=0x{resp.Status:X8}");
}

// ---------- LOGOFF ----------
{
    var body = new byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 4);
    var resp = await ExchangeAsync(stream, 0x0002, body, msgId++, sessionId, treeId);
    Console.WriteLine($"LOGOFF: status=0x{resp.Status:X8}");
}

return 0;

// ---------- helpers ----------

static async Task<(uint Status, ulong SessionId, uint TreeId, byte[] Body)> ExchangeAsync(
    NetworkStream s, ushort command, byte[] body, ulong msgId, ulong sessionId, uint treeId)
{
    var header = new byte[64];
    header[0] = 0xFE;
    header[1] = (byte)'S';
    header[2] = (byte)'M';
    header[3] = (byte)'B';
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), 1);   // CreditCharge
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0);    // Status
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), command);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), 1);   // CreditRequest
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24, 8), msgId);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), treeId);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40, 8), sessionId);

    var packet = new byte[64 + body.Length];
    header.CopyTo(packet, 0);
    body.CopyTo(packet, 64);

    var nbt = new byte[4];
    nbt[1] = (byte)((packet.Length >> 16) & 0xFF);
    nbt[2] = (byte)((packet.Length >> 8) & 0xFF);
    nbt[3] = (byte)(packet.Length & 0xFF);
    await s.WriteAsync(nbt);
    await s.WriteAsync(packet);
    await s.FlushAsync();

    var nbtIn = new byte[4];
    await ReadExact(s, nbtIn);
    var len = (nbtIn[1] << 16) | (nbtIn[2] << 8) | nbtIn[3];
    var resp = new byte[len];
    await ReadExact(s, resp);

    var status = BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(8, 4));
    var tid = BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(36, 4));
    var sid = BinaryPrimitives.ReadUInt64LittleEndian(resp.AsSpan(40, 8));
    var respBody = resp.AsSpan(64).ToArray();
    return (status, sid, tid, respBody);
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
