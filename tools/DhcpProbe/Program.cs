// Tiny PXE DHCPDISCOVER simulator used to smoke-test Everboot's DHCP proxy
// without needing real PXE hardware. Sends one DISCOVER with a configurable
// arch / user-class, prints the parsed OFFER it gets back.
//
// usage: dotnet run --project tools/DhcpProbe -- [host] [port] [arch] [iPXE]
//   host    default 127.0.0.1
//   port    default 6767
//   arch    decimal int (0 BIOS, 7 EFI x64, 11 EFI ARM64). default 7
//   iPXE    "ipxe" to send user-class iPXE. default not set
//
// example:
//   dotnet run --project tools/DhcpProbe -- 127.0.0.1 6767 0
//   dotnet run --project tools/DhcpProbe -- 127.0.0.1 6767 7 ipxe

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 6767;
var arch = (ushort)(args.Length > 2 ? int.Parse(args[2]) : 7);
var ipxe = args.Length > 3 && args[3].Equals("ipxe", StringComparison.OrdinalIgnoreCase);

var xid = (uint)Random.Shared.Next();
var mac = new byte[] { 0x52, 0x54, 0x00, 0xab, 0xcd, 0xef };

var request = BuildDiscover(xid, mac, arch, ipxe);

using var udp = new UdpClient(0); // OS-picked source port
udp.Client.ReceiveTimeout = 5000;

await udp.SendAsync(request, request.Length, host, port);
Console.WriteLine($"sent {request.Length} bytes -> {host}:{port}  xid=0x{xid:x8} arch={arch} ipxe={ipxe}");

var remote = new IPEndPoint(IPAddress.Any, 0);
byte[] reply;
try
{
    reply = udp.Receive(ref remote);
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
{
    Console.Error.WriteLine("no reply within 5s");
    return 1;
}

Console.WriteLine($"received {reply.Length} bytes <- {remote}");
PrintReply(reply);
return 0;

static byte[] BuildDiscover(uint xid, byte[] mac, ushort arch, bool ipxe)
{
    var packet = new byte[1024];
    var pos = 0;

    packet[pos++] = 1;          // op = BOOTREQUEST
    packet[pos++] = 1;          // htype = ethernet
    packet[pos++] = 6;          // hlen
    packet[pos++] = 0;          // hops

    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(pos, 4), xid); pos += 4;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), 0);   pos += 2; // secs
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), 0);   pos += 2; // flags (0 = no broadcast bit)

    pos += 16; // ciaddr, yiaddr, siaddr, giaddr - all zero

    Array.Copy(mac, 0, packet, pos, 6);
    pos += 16; // chaddr

    pos += 64;  // sname
    pos += 128; // file

    packet[pos++] = 0x63;
    packet[pos++] = 0x82;
    packet[pos++] = 0x53;
    packet[pos++] = 0x63;

    // option 53: DHCP message type = DISCOVER
    packet[pos++] = 53; packet[pos++] = 1; packet[pos++] = 1;

    // option 60: vendor class id
    var vci = Encoding.ASCII.GetBytes("PXEClient:Arch:00007:UNDI:003016");
    packet[pos++] = 60; packet[pos++] = (byte)vci.Length;
    Array.Copy(vci, 0, packet, pos, vci.Length); pos += vci.Length;

    // option 93: client architecture
    packet[pos++] = 93; packet[pos++] = 2;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos, 2), arch); pos += 2;

    // option 94: client network interface (UNDI 3.0)
    packet[pos++] = 94; packet[pos++] = 3; packet[pos++] = 1; packet[pos++] = 3; packet[pos++] = 0;

    // option 77: user class = "iPXE" when simulating iPXE chainload
    if (ipxe)
    {
        var uc = Encoding.ASCII.GetBytes("iPXE");
        packet[pos++] = 77; packet[pos++] = (byte)uc.Length;
        Array.Copy(uc, 0, packet, pos, uc.Length); pos += uc.Length;
    }

    packet[pos++] = 255; // end

    var sized = new byte[Math.Max(pos, 300)];
    Array.Copy(packet, sized, pos);
    return sized;
}

static void PrintReply(byte[] reply)
{
    if (reply.Length < 240) { Console.WriteLine("reply too small to be DHCP"); return; }
    var op = reply[0];
    var xid = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4, 4));
    var siaddr = new IPAddress(reply.AsSpan(20, 4).ToArray());

    var sname = ReadStr(reply.AsSpan(44, 64));
    var file = ReadStr(reply.AsSpan(108, 128));

    Console.WriteLine($"  op       = {(op == 2 ? "BOOTREPLY" : op.ToString())}");
    Console.WriteLine($"  xid      = 0x{xid:x8}");
    Console.WriteLine($"  siaddr   = {siaddr}");
    Console.WriteLine($"  sname    = '{sname}'");
    Console.WriteLine($"  file     = '{file}'");

    if (reply.Length < 244) { return; }
    if (reply[236] != 0x63 || reply[237] != 0x82 || reply[238] != 0x53 || reply[239] != 0x63)
    {
        Console.WriteLine("  (no DHCP magic cookie)");
        return;
    }

    Console.WriteLine("  options:");
    var pos = 240;
    while (pos < reply.Length)
    {
        var tag = reply[pos++];
        if (tag == 255) break;
        if (tag == 0) continue;
        if (pos >= reply.Length) break;
        var len = reply[pos++];
        if (pos + len > reply.Length) break;
        var value = reply.AsSpan(pos, len).ToArray();
        pos += len;
        Console.WriteLine($"    [{tag}] {Describe(tag, value)}");
    }
}

static string ReadStr(ReadOnlySpan<byte> bytes)
{
    var end = bytes.IndexOf((byte)0);
    if (end < 0) end = bytes.Length;
    return end == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..end]);
}

static string Describe(byte tag, byte[] v) => tag switch
{
    53 => $"MessageType = {v[0]} ({MessageTypeName(v[0])})",
    54 => $"ServerIdentifier = {new IPAddress(v)}",
    60 => $"VendorClassIdentifier = '{Encoding.ASCII.GetString(v)}'",
    66 => $"TftpServerName = '{Encoding.ASCII.GetString(v)}'",
    67 => $"BootFileName = '{Encoding.ASCII.GetString(v)}'",
    _ => string.Join(' ', v.Select(b => b.ToString("x2"))),
};

static string MessageTypeName(byte b) => b switch
{
    1 => "DISCOVER", 2 => "OFFER", 3 => "REQUEST", 4 => "DECLINE",
    5 => "ACK", 6 => "NAK", 7 => "RELEASE", 8 => "INFORM",
    _ => "?",
};
