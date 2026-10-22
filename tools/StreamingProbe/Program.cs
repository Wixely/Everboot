using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DiscUtils.Iso9660;
using Everboot.Configuration;
using Everboot.Services;
using Everboot.Services.Nfs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Dependency-free integration suite. Fixtures are deliberately NOT bootable OSes.
// --serve keeps the fixture server alive for an independent Linux mount test.
var root = Path.Combine(Path.GetTempPath(), "everboot-streaming-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "isos"));
var isoPath = Path.Combine(root, "isos", "ubuntu.iso");
var payload = new byte[12 * 1024 * 1024 + 37];
new Random(20261022).NextBytes(payload);
MakeIso(isoPath, "Ubuntu Live Test", "casper", true);
MakeIso(Path.Combine(root, "isos", "debian.iso"), "Debian Live Test", "live", true);
MakeIso(Path.Combine(root, "isos", "incomplete.iso"), "Ubuntu Incomplete", "casper", false);
MakeIso(Path.Combine(root, "isos", "advanced.iso"), "Unknown", "other", true);
var config = new EverbootOptions { DataDirectory = root, CatalogRescanIntervalSeconds = 0 };
config.Nfs.BindAddress = "127.0.0.1";
config.Nfs.Port = 32049; config.Nfs.MountPort = 32048; config.Nfs.PortmapperPort = 32111;
var options = Options.Create(config);
var inspector = new IsoInspector(NullLogger<IsoInspector>.Instance);
using var catalog = new BootCatalog(options, inspector, NullLogger<BootCatalog>.Instance);
using var protocol = new NfsProtocol(catalog, config.Nfs);
var status = new NfsStatus();
using var service = new NfsService(catalog, options, status, NullLogger<NfsService>.Instance);
var checks = 0;
try
{
    var generator = new BootConfigGenerator(catalog, options);
    var menu = generator.GenerateIpxeMenu("192.0.2.1:8080");
    Check(menu.Contains("item --key 4 ubuntu-auto"), "single selection routes directly to Auto");
    Check(menu.Contains("netboot=nfs nfsroot=192.0.2.1:/ubuntu"), "Casper NFS recipe uses ISO export");
    Check(menu.Contains("boot=live components netboot=nfs"), "Debian Live NFS recipe");
    Check(menu.Contains("mountport=32048") && menu.Contains("port=32049"), "custom NFS ports propagated");
    Check(!menu.Contains(":incomplete-direct-"), "missing initrd excludes direct recipes");
    Check(menu.Split('\n').Where(l => l.StartsWith(':')).GroupBy(l => l.Trim()).All(g => g.Count() == 1), "reserved image name cannot collide with menu label");
    Check(!menu.Contains("netboot=httpfs"), "misleading Casper httpfs recipe removed");
    Check(menu.Split('\n').First(l => l.StartsWith("choose ")).Contains(catalog.Entries[0].Slug + "-auto"), "sorted menu preserves correct default");
    config.Nfs.Enabled = false;
    Check(!generator.GenerateIpxeMenu("192.0.2.1:8080").Contains("netboot=nfs"), "disabled NFS removes all streaming recipes");
    config.Nfs.Enabled = true;
    config.Menu.OneClickBoot = false;
    Check(generator.GenerateIpxeMenu("192.0.2.1:8080").Contains("item --key 4 ubuntu "), "legacy submenu mode remains available");
    config.Menu.OneClickBoot = true;
    config.Iscsi.UseForSanboot = true; config.Iscsi.Port = 33260;
    Check(generator.GenerateIpxeMenu("192.0.2.1:8080").Contains("iscsi:192.0.2.1::33260::"), "iSCSI port included and handoff not suppressed");

    var pmap = Call(100000, 3, w => { w.U(100003); w.U(3); w.U(6); w.U(0); }, 2);
    Check(pmap.U() == 32049, "portmapper TCP mapping");
    pmap = Call(100000, 3, w => { w.U(100003); w.U(3); w.U(17); w.U(0); }, 2);
    Check(pmap.U() == 0, "unsupported UDP not advertised");
    var mount = Call(100005, 1, w => w.S("/ubuntu"));
    Check(mount.U() == 0, "MOUNTv3 succeeds");
    var handle = mount.B();
    Check(handle.Length == 20, "generation-scoped handle");
    Check(mount.U() == 2 && mount.U() == 0 && mount.U() == 1, "AUTH_NONE and AUTH_SYS advertised");
    var missing = Call(100005, 1, w => w.S("/../ubuntu"));
    Check(missing.U() == 2, "export traversal rejected");
    var attr = Nfs(1, handle);
    Check(attr.U() == 0 && attr.U() == 2, "root GETATTR is a directory");
    var dirHandle = Lookup(handle, "casper");
    var fileHandle = Lookup(dirHandle, "filesystem.squashfs");
    var escaped = Call(100003, 3, w => { w.B(dirHandle); w.S("../../readme.txt"); });
    Check(escaped.U() == 2, "LOOKUP rejects path components");
    var reads = new[] { (0ul, 4096u), (2047ul, 3000u), (999_999ul, uint.MaxValue), ((ulong)payload.Length - 9, 100u), (ulong.MaxValue, 4096u), (0ul, 0u) };
    foreach (var (offset, count) in reads)
    {
        var read = Call(100003, 6, w => { w.B(fileHandle); w.L(offset); w.U(count); });
        Check(read.U() == 0, "READ succeeds"); SkipAttrs(read);
        var length = read.U(); var eof = read.U(); var bytes = read.B();
        var expectedLength = offset >= (ulong)payload.Length ? 0 : (int)Math.Min(Math.Min(count, 65536), (ulong)payload.Length - offset);
        Check(length == expectedLength && bytes.Length == expectedLength, "READ count bounded at 64 KiB and EOF");
        Check(bytes.SequenceEqual(payload.Skip((int)Math.Min(offset, (ulong)payload.Length)).Take(expectedLength)), "random-access bytes match original ISO file");
        Check(eof == (offset >= (ulong)payload.Length || offset + (ulong)expectedLength >= (ulong)payload.Length ? 1 : 0), "READ EOF flag");
    }
    foreach (var proc in new uint[] { 2, 7, 8, 9, 10, 11, 12, 13, 14, 15, 21 })
        Check(Nfs(proc, fileHandle).U() == 30, "mutation rejected as read-only");
    var access = Call(100003, 4, w => { w.B(fileHandle); w.U(63); });
    Check(access.U() == 0, "ACCESS succeeds"); SkipAttrs(access);
    Check(access.U() == 1, "ACCESS grants read only");
    Check(Nfs(1, new byte[20]).U() == 70, "unknown handle returns STALE");
    var names = new List<string>(); ulong cookie = 0; var verifier = new byte[8];
    var pages = 0;
    while (true)
    {
        var page = Call(100003, 17, w => { w.B(dirHandle); w.L(cookie); w.Raw(verifier); w.U(512); w.U(1024); });
        Check(page.U() == 0, "READDIRPLUS succeeds"); SkipAttrs(page); verifier = page.Take(8);
        while (page.U() == 1)
        {
            page.L(); names.Add(page.S()); cookie = page.L(); SkipAttrs(page);
            Check(page.U() == 1 && page.B().Length == 20, "READDIRPLUS includes valid handle");
        }
        pages++;
        if (page.U() == 1) break;
        if (pages > 300) throw new Exception("Directory pagination did not terminate.");
    }
    Check(pages > 1 && names.Count == 203 && names.Distinct().Count() == 203, "directory pagination has no gaps or duplicates");
    var badCookie = Call(100003, 16, w => { w.B(dirHandle); w.L(1); w.Raw(new byte[8]); w.U(1024); });
    Check(badCookie.U() == 10003, "wrong cookie verifier rejected");
    var tiny = Call(100003, 16, w => { w.B(dirHandle); w.L(0); w.Raw(new byte[8]); w.U(10); });
    Check(tiny.U() == 10005, "small directory buffer rejected");
    Check(Nfs(19, handle).U() == 0 && Nfs(20, handle).U() == 0 && Nfs(18, handle).U() == 0, "filesystem info procedures");
    var malformed = Request(100003, 6, w => w.U(uint.MaxValue));
    var bad = new Wire(protocol.Dispatch(malformed, 100003)); bad.Take(20);
    Check(bad.U() == 4, "oversized XDR field returns GARBAGE_ARGS");

    // Fixed ports are only for the manual mount fixture. Normal test runs use
    // ephemeral ports so they can coexist with development servers and WSL.
    if (!args.Contains("--serve"))
    {
        var ports = new HashSet<int>();
        while (ports.Count < 3) ports.Add(FreePort());
        var selected = ports.ToArray();
        config.Nfs.Port = selected[0]; config.Nfs.MountPort = selected[1]; config.Nfs.PortmapperPort = selected[2];
    }
    await service.StartAsync(default);
    for (var i = 0; i < 300 && !status.Running && service.ExecuteTask?.IsCompleted != true; i++) await Task.Delay(100);
    if (service.ExecuteTask?.IsFaulted == true) await service.ExecuteTask;
    Check(status.Running, $"real TCP listeners started (task={service.ExecuteTask?.Status}, enabled={config.Nfs.Enabled})");
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, config.Nfs.Port);
        var stream = client.GetStream();
        var request = Request(100003, 0, _ => { });
        // Two fragments, deliberately splitting an XDR word.
        await Frame(stream, request[..7], false); await Frame(stream, request[7..], true);
        var response = await NfsService.ReadRecordAsync(stream, new CancellationTokenSource(5000).Token);
        Check(response is { Length: 24 }, "fragmented RPC accepted over TCP");
        await Frame(stream, Request(100003, 0, _ => { }), true);
        response = await NfsService.ReadRecordAsync(stream, new CancellationTokenSource(5000).Token);
        Check(response is { Length: 24 }, "persistent TCP session handles second request");
    }
    var huge = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(huge, 0x80100001);
    try { await NfsService.ReadRecordAsync(new MemoryStream(huge), default); throw new Exception("Oversized frame accepted."); }
    catch (InvalidDataException) { checks++; }
    config.Http.BindAddress = "localhost"; config.Http.Port = FreePort();
    using (var http = new HttpFileService(NullLogger<HttpFileService>.Instance, catalog, generator, options, status))
    using (var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{config.Http.Port}"), Timeout = TimeSpan.FromSeconds(5) })
    {
        await http.StartAsync(default);
        try
        {
            string? html = null;
            for (var i = 0; i < 100 && html is null; i++)
            {
                try { html = await client.GetStringAsync("/"); }
                catch (HttpRequestException) { await Task.Delay(20); }
            }
            Check(html?.Contains("NFS: Listening") == true && html.Contains("Streaming candidate"), "HTTP library reports live service and compatibility");
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
            Check(head.IsSuccessStatusCode && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "library HEAD has no body");
            var request = new HttpRequestMessage(HttpMethod.Get, "/iso/ubuntu/casper/filesystem.squashfs");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2047, 5046);
            using var range = await client.SendAsync(request);
            Check(range.StatusCode == HttpStatusCode.PartialContent && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(payload.Skip(2047).Take(3000)), "HTTP range and NFS expose identical ISO content");
            Check((await client.GetStringAsync("/boot.ipxe")).Contains("netboot=nfs"), "HTTP serves streaming menu");
        }
        finally { await http.StopAsync(default); }
    }
    Console.WriteLine($"PASS: {checks} checks. Synthetic ISO protocol tests; not an OS boot test.");
    if (args.Contains("--serve"))
    {
        Console.WriteLine("Fixture NFS export: 127.0.0.1:/ubuntu (port=32049,mountport=32048,vers=3,proto=tcp,mountproto=tcp,nolock,ro)");
        Console.WriteLine("Expected SHA256: " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant());
        Console.WriteLine("Serving for 10 minutes. Ctrl+C stops the fixture process.");
        using var serving = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; serving.Cancel(); };
        try { await Task.Delay(Timeout.InfiniteTimeSpan, serving.Token); }
        catch (OperationCanceledException) { }
    }
    await service.StopAsync(default);
    File.Move(isoPath, isoPath + ".removed"); catalog.ForceRescan();
    Check(Nfs(1, fileHandle).U() == 70, "removed image invalidates old handles");
    Console.WriteLine($"PASS: {checks} checks including image removal.");
    return 0;
}
finally
{
    await service.StopAsync(default);
    protocol.Dispose(); catalog.Dispose();
    Directory.Delete(root, recursive: true); // this process's unique temporary fixture directory
}

void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; }
void MakeIso(string path, string label, string dir, bool initrd)
{
    var b = new CDBuilder { UseJoliet = true, VolumeIdentifier = label };
    b.AddFile(dir + "\\vmlinuz", new byte[1024]);
    if (initrd) b.AddFile(dir + (dir == "live" ? "\\initrd.img" : "\\initrd"), new byte[1024]);
    b.AddFile(dir + "\\filesystem.squashfs", payload);
    for (var i = 0; i < 200; i++) b.AddFile(dir + $"\\part-{i:D3}.txt", Encoding.ASCII.GetBytes($"part {i}\n"));
    b.Build(path);
}
Wire Call(uint program, uint procedure, Action<Wire> body, uint version = 3)
{
    var response = new Wire(protocol.Dispatch(Request(program, procedure, body, version), program));
    Check(response.U() == 42 && response.U() == 1 && response.U() == 0, "RPC reply header");
    Check(response.U() == 0 && response.U() == 0 && response.U() == 0, "RPC accepted success");
    return response;
}
Wire Nfs(uint proc, byte[] fh) => Call(100003, proc, w => w.B(fh));
byte[] Lookup(byte[] parent, string name)
{
    var r = Call(100003, 3, w => { w.B(parent); w.S(name); });
    Check(r.U() == 0, "LOOKUP " + name); return r.B();
}
static byte[] Request(uint program, uint procedure, Action<Wire> body, uint version = 3)
{
    var w = new Wire();
    foreach (var n in new[] { 42u, 0u, 2u, program, version, procedure, 0u, 0u, 0u, 0u }) w.U(n);
    body(w); return w.Data();
}
static void SkipAttrs(Wire r) { if (r.U() == 1) r.Take(84); }
static async Task Frame(Stream stream, byte[] bytes, bool last)
{
    var marker = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(marker, (uint)bytes.Length | (last ? 0x80000000u : 0));
    await stream.WriteAsync(marker); await stream.WriteAsync(bytes);
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

// Independent wire encoder/decoder, intentionally does not use production XDR code.
sealed class Wire
{
    private readonly MemoryStream stream;
    public Wire() => stream = new();
    public Wire(byte[] bytes) => stream = new(bytes);
    public byte[] Data() => stream.ToArray();
    public void U(uint n) { for (var i = 24; i >= 0; i -= 8) stream.WriteByte((byte)(n >> i)); }
    public uint U() { uint n = 0; for (var i = 0; i < 4; i++) { var b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); n = n << 8 | (byte)b; } return n; }
    public void L(ulong n) { U((uint)(n >> 32)); U((uint)n); }
    public ulong L() => (ulong)U() << 32 | U();
    public void Raw(byte[] b) => stream.Write(b);
    public byte[] Take(int count) { var bytes = new byte[count]; stream.ReadExactly(bytes); return bytes; }
    public void B(byte[] bytes) { U((uint)bytes.Length); Raw(bytes); while (stream.Position % 4 != 0) stream.WriteByte(0); }
    public byte[] B() { var n = checked((int)U()); var bytes = Take(n); Take((4 - n % 4) % 4); return bytes; }
    public void S(string s) => B(Encoding.UTF8.GetBytes(s));
    public string S() => Encoding.UTF8.GetString(B());
}
