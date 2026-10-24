using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DiscUtils.Iso9660;
using Everboot.Configuration;
using Everboot.Services;
using Everboot.Services.Nfs;
using Everboot.Services.Vm;
using System.Text.Json;
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
    var fakeIo = new byte[2500]; fakeIo[0] = (byte)'M'; fakeIo[1] = (byte)'Z';
    fakeIo[512] = (byte)'B'; fakeIo[513] = (byte)'J'; fakeIo[2046] = (byte)'M'; fakeIo[2047] = (byte)'S';
    var dosFiles = new Dictionary<string, byte[]> { ["COMMAND.COM"] = [1, 2, 3], ["IO.SYS"] = fakeIo };
    var dosImage = DosStartupImage.Build(dosFiles);
    Check(dosImage.Length == 1474560 && dosImage[510] == 0x55 && dosImage[511] == 0xaa, "own bootstrap produces a standard FAT12 boot image");
    Check(Encoding.ASCII.GetString(dosImage, 19 * 512, 11) == "IO      SYS" && dosImage.AsSpan(33 * 512, fakeIo.Length).SequenceEqual(fakeIo), "DOS loader is first and contiguous regardless of input order");
    Check(dosImage.AsSpan(512, 4608).SequenceEqual(dosImage.AsSpan(5120, 4608)), "startup FAT copies agree");
    Check((BinaryPrimitives.ReadUInt16LittleEndian(dosImage.AsSpan(515)) & 0xfff) == 3 &&
        (BinaryPrimitives.ReadUInt16LittleEndian(dosImage.AsSpan(521)) & 0xfff) == 0xfff, "startup FAT links terminate after the loader's final cluster");
    try { DosStartupImage.Build(new Dictionary<string, byte[]> { ["IO.SYS"] = new byte[2048] }); throw new Exception("bad DOS loader accepted"); }
    catch (IOException) { checks++; }
    try { DosStartupImage.Build(new Dictionary<string, byte[]> { ["IO.SYS"] = fakeIo, ["LARGE.BIN"] = new byte[1474560] }); throw new Exception("oversized DOS disk accepted"); }
    catch (IOException) { checks++; }
    var initialDisk = Path.Combine(root, "initial-dos.raw");
    DosInstallDisk.Create(initialDisk);
    using (var file = File.OpenRead(initialDisk))
    {
        var sector = new byte[512]; file.ReadExactly(sector);
        Check(file.Length == 2L << 30 && sector[446] == 0x80 && sector[450] == 0x0e && BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(454)) == 63, "new DOS install disk has a bounded active FAT16 partition");
        file.Position = 63L * 512; file.ReadExactly(sector);
        var clusters = (DosInstallDisk.PartitionSectors - 1 - 512 - 32) / sector[13];
        Check(clusters is >= 4085 and < 65525 && sector[510] == 0x55 && sector[511] == 0xaa && BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(28)) == 63, "FAT16 geometry and hidden sectors are consistent");
    }
    try { DosInstallDisk.Create(initialDisk); throw new Exception("existing disk overwritten"); }
    catch (IOException) { checks++; }
    File.Delete(initialDisk);
    Check(!JsonSerializer.Serialize(new VmBootRecipe("cd")).Contains("InitialDisk"), "adding disk initialization preserves old profile identities");
    try { VmRecipes.Validate(new("disk", InitialDisk: "fat16")); throw new Exception("raw source initialization accepted"); }
    catch (ArgumentException) { checks++; }
    if (OperatingSystem.IsWindows())
    {
        var cab = Path.Combine(root, "test.cab");
        // Uncompressed, single-folder CAB fixture; no Microsoft executable bytes.
        var cabName = Encoding.ASCII.GetBytes("COMMAND.COM\0");
        var dataOffset = 44 + 16 + cabName.Length;
        using (var writer = new BinaryWriter(File.Create(cab)))
        {
            writer.Write(Encoding.ASCII.GetBytes("MSCF")); writer.Write(0); writer.Write(dataOffset + 8 + 3); writer.Write(0);
            writer.Write(44); writer.Write(0); writer.Write((byte)3); writer.Write((byte)1);
            writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write(dataOffset); writer.Write((ushort)1); writer.Write((ushort)0);
            writer.Write(3); writer.Write(0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)32); writer.Write(cabName);
            writer.Write(0); writer.Write((ushort)3); writer.Write((ushort)3); writer.Write(new byte[] { 1, 2, 3 });
        }
        var cabOutput = Path.Combine(root, "cab-output"); Directory.CreateDirectory(cabOutput);
        WindowsCabinet.Extract(cab, cabOutput, new(StringComparer.OrdinalIgnoreCase) { "COMMAND.COM" });
        Check(File.ReadAllBytes(Path.Combine(cabOutput, "COMMAND.COM")).SequenceEqual(new byte[] { 1, 2, 3 }), "Windows cabinet API extracts exact whitelisted bytes");
        File.Delete(Path.Combine(cabOutput, "COMMAND.COM"));
        WindowsCabinet.Extract(cab, cabOutput, new(StringComparer.OrdinalIgnoreCase) { "IO.SYS" });
        Check(!Directory.EnumerateFiles(cabOutput).Any(), "cabinet extraction skips files outside the whitelist");
    }
    var generator = new BootConfigGenerator(catalog, options);
    Check(DistroProfiles.Match("d-live 13.7.0 st", p => p == "live/filesystem.squashfs")?.Id == "debian-live", "current Debian Live volume label selects NFS-capable profile");
    Check(DistroProfiles.Match("d-live 13.7.0 st", _ => false) is null, "Debian Live label alone does not establish a live filesystem");
    var menu = generator.GenerateIpxeMenu("192.0.2.1:8080");
    Check(menu.Contains("item --key 4 ubuntu-auto"), "single selection routes directly to Auto");
    Check(menu.Contains("netboot=nfs nfsroot=192.0.2.1:/ubuntu"), "Casper NFS recipe uses ISO export");
    Check(menu.Contains("boot=live components netboot=nfs"), "Debian Live NFS recipe");
    Check(menu.Contains("nfsopts=ro,nolock,vers=3,tcp,port=32049,rsize=65536") && !menu.Contains("proto=") && !menu.Contains("mountport="),
        "PXE NFS options work with klibc nfsmount; MOUNT is discovered via portmapper");
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

    // Synthetic legacy media validates routing, not successful OS boot.
    var legacy = new CDBuilder { UseJoliet = true, VolumeIdentifier = "Legacy Test" };
    legacy.AddFile("win98/setup.exe", new byte[16]);
    legacy.Build(Path.Combine(root, "isos", "win98.iso"));
    var xp = new CDBuilder { UseJoliet = true, VolumeIdentifier = "XP Test" };
    xp.AddFile("i386/setupldr.bin", new byte[16]);
    xp.Build(Path.Combine(root, "isos", "xp.iso"));
    File.WriteAllBytes(Path.Combine(root, "isos", "dos.img"), new byte[1440 * 1024]);
    using (var disk = File.Create(Path.Combine(root, "isos", "disk.img"))) disk.SetLength(8 * 1024 * 1024);
    catalog.ForceRescan();
    menu = generator.GenerateIpxeMenu("192.0.2.1:8080");
    string Method(string label) => menu.Replace("\r\n", "\n", StringComparison.Ordinal).Split(":" + label + "\n", StringSplitOptions.None)[1].Split("\n:", StringSplitOptions.None)[0];
    Check(catalog.Entries.Single(e => e.Slug == "win98").Metadata.WindowsEra == WindowsBootEra.NinexClass, "9x fixture detected");
    var ubuntuInstaller = DistroProfiles.Match("Ubuntu-Server 10", p => p == "install/vmlinuz");
    var slitaz = DistroProfiles.Match("SliTaz core-4in1", p => p == "boot/bzImage");
    Check(ubuntuInstaller?.Id == "ubuntu-server-classic" && ubuntuInstaller.DirectBoots.Length == 0,
        "classic Ubuntu installer recognised without unverified PXE recipe");
    Check(QemuLaunch.RecommendedProfile(new(IsoLayout.Iso9660, WindowsBootEra.None, null, ubuntuInstaller, null, null)) == "modern",
        "classic Ubuntu installer gets a 64-bit CPU");
    Check(slitaz?.Id == "slitaz" && QemuLaunch.RecommendedProfile(new(IsoLayout.Iso9660, WindowsBootEra.None, null, slitaz, null, null)) == "classic",
        "SliTaz gets enough RAM to expand its live filesystem");
    Check(DistroProfiles.Match("SliTaz core-4in1", _ => false) is null && DistroProfiles.Match("Ubuntu-Server 10", _ => false) is null,
        "new profiles require media contents as well as labels");
    Check(Method("win98-auto").Contains("goto win98-saniscsi"), "9x has a one-selection compatibility attempt");
    Check(Method("win98-sanhttp").Contains("iseq ${platform} pcbios || goto win98-needs-bios"), "9x HTTP path requires BIOS");
    Check(Method("xp-saniscsi").Contains("iseq ${platform} pcbios || goto xp-needs-bios"), "XP iSCSI path requires BIOS");
    Check(Method("win98-sanhttp").Contains("sanboot --drive 0xe0 --no-describe"), "ISO occupies CD drive instead of local hard disk number");
    Check(Method("disk-sanhttp").Contains("sanboot --drive 0x80 --no-describe"), "raw hard disk retains BIOS boot drive number");
    Check(Method("dos-memdisk").Contains("memdisk floppy raw"), "DOS floppy is not misrepresented as an ISO");
    Check(Method("disk-memdisk").Contains("memdisk harddisk raw"), "large raw disk uses harddisk mode");
    Check(Method("win98-memdisk").Contains("memdisk iso raw"), "ISO retains MEMDISK ISO mode");
    Check(Method("dos-memdisk").IndexOf("iseq ${platform} pcbios", StringComparison.Ordinal) < Method("dos-memdisk").IndexOf("kernel http", StringComparison.Ordinal), "MEMDISK rejects UEFI before downloading");
    Check(menu.Contains("Everboot SMB2 shares cannot be used by stock DOS/9x"), "legacy share limitation explained");
    Check(menu.Split('\n').Where(l => l.StartsWith(':')).GroupBy(l => l.Trim()).All(g => g.Count() == 1), "legacy methods have unique labels");

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
            var vmImages = JsonSerializer.Deserialize<VmImage[]>(await client.GetStringAsync("/catalog.json"))!;
            Check(vmImages.Length == catalog.Entries.Count && vmImages.All(i => i.Path.StartsWith("/isos/")), "VM catalog exposes relative media paths without server filesystem paths");
            Check(vmImages.Single(i => i.Path == "/isos/win98.iso").Profile == "legacy" && vmImages.Single(i => i.Path == "/isos/xp.iso").Profile == "classic", "legacy VM presets selected automatically");
            var vmImage = vmImages.Single(i => i.Path == "/isos/ubuntu.iso");
            var media = QemuLaunch.MediaUri(client.BaseAddress!, vmImage);
            await QemuLaunch.CheckRangeAsync(client, media, vmImage.Size, default);
            Check(true, "QEMU preflight validates real HTTP range response");
            try { await QemuLaunch.CheckRangeAsync(client, media, vmImage.Size + 1, default); throw new Exception("changed media accepted"); }
            catch (IOException) { checks++; }
            try { QemuLaunch.MediaUri(client.BaseAddress!, vmImage with { Path = "//example.org/image.iso" }); throw new Exception("foreign media accepted"); }
            catch (ArgumentException) { checks++; }
            using var bridge = new HttpBlockBridge(media, vmImage.Size);
            using (var nbd = new TcpClient())
            {
                await nbd.ConnectAsync(IPAddress.Loopback, bridge.Port);
                var stream = nbd.GetStream();
                var hello = new byte[152]; await stream.ReadExactlyAsync(hello);
                Check(BinaryPrimitives.ReadUInt64BigEndian(hello) == 0x4e42444d41474943 &&
                    BinaryPrimitives.ReadUInt64BigEndian(hello.AsSpan(16)) == (ulong)vmImage.Size &&
                    BinaryPrimitives.ReadUInt32BigEndian(hello.AsSpan(24)) == 3, "NBD handshake advertises exact size and read-only flag");
                var command = new byte[28];
                BinaryPrimitives.WriteUInt32BigEndian(command, 0x25609513);
                BinaryPrimitives.WriteUInt64BigEndian(command.AsSpan(8), 123);
                BinaryPrimitives.WriteUInt64BigEndian(command.AsSpan(16), 2047);
                BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(24), 4096);
                await stream.WriteAsync(command);
                var reply = new byte[16]; await stream.ReadExactlyAsync(reply);
                var content = new byte[4096]; await stream.ReadExactlyAsync(content);
                var expected = new byte[4096]; using (var source = File.OpenRead(isoPath)) { source.Position = 2047; source.ReadExactly(expected); }
                Check(BinaryPrimitives.ReadUInt32BigEndian(reply) == 0x67446698 && BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) == 0 &&
                    BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8)) == 123 && content.SequenceEqual(expected), "NBD random read matches ISO bytes across sector boundaries");
                BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(6), 1);
                BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(24), 512);
                await stream.WriteAsync(command); await stream.WriteAsync(new byte[512]); await stream.ReadExactlyAsync(reply);
                Check(BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) == 1, "NBD writes rejected and payload drained");
                BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(6), 0);
                BinaryPrimitives.WriteUInt64BigEndian(command.AsSpan(16), ulong.MaxValue);
                await stream.WriteAsync(command); await stream.ReadExactlyAsync(reply);
                Check(BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) == 22, "NBD overflowing offset rejected");
                BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(24), uint.MaxValue);
                await stream.WriteAsync(command);
                Check(await stream.ReadAsync(reply) == 0, "NBD oversized request closes connection without allocation");
            }
            using var block = JsonDocument.Parse(bridge.Block);
            Check(block.RootElement.GetProperty("file").GetProperty("driver").GetString() == "nbd" && block.RootElement.GetProperty("read-only").GetBoolean(), "media uses read-only local NBD bridge");
            var vmArgs = QemuLaunch.Arguments(bridge.Block, "legacy", "disk,with spaces.qcow2", true, false);
            Check(vmArgs.Contains("ide-cd,drive=media,bus=ide.1,unit=0") && vmArgs.Contains("128") && vmArgs.Contains("none"), "legacy VM has IDE CD and small RAM preset");
            var vmCommand = QemuLaunch.Command("qemu", vmArgs);
            Check(!vmCommand.UseShellExecute && vmCommand.ArgumentList.Contains(vmArgs[vmArgs.IndexOf("-blockdev") + 1]), "QEMU arguments bypass shell expansion");
            var vmHtml = VmClient.Html([vmImage with { Name = "<script>alert(1)</script>" }], "test-token", "<error>", 1);
            Check(!vmHtml.Contains("<script>") && vmHtml.Contains("&lt;error&gt;") && vmHtml.Contains(" disabled"), "VM library encodes remote names and disables duplicate launches");
            Check(vmArgs.Contains("order=cd,once=d,menu=on") && QemuLaunch.Arguments(bridge.Block, "legacy", "disk.qcow2", true, false, false).Contains("order=cd,menu=on"),
                "new machines boot installation CD once; saved machines prefer their hard disk");
            var startup = Path.Combine(catalog.TftpDirectory, "startup.img");
            File.WriteAllBytes(startup, payload[..4096]);
            var sidecar = Path.Combine(root, "isos", "win98.iso.vm.json");
            File.WriteAllText(sidecar, "{\"Media\":\"cd\",\"Profile\":\"legacy\",\"Floppy\":\"startup.img\"}");
            var configured = JsonSerializer.Deserialize<VmImage[]>(await client.GetStringAsync("/catalog.json"))!.Single(i => i.Path == "/isos/win98.iso");
            Check(configured.Problem is null && configured.Boot?.Floppy?.Size == 4096, "sidecar resolves startup media through catalog");
            var asset = configured.Boot!.Floppy!;
            var assetDirectory = Path.Combine(root, "asset-cache");
            var staged = await VmRecipes.StageAsync(client.BaseAddress!, asset, assetDirectory, default);
            Check(File.ReadAllBytes(staged).SequenceEqual(payload[..4096]), "startup media staged through HTTP with exact content");
            File.WriteAllBytes(staged, new byte[4096]);
            await VmRecipes.StageAsync(client.BaseAddress!, asset, assetDirectory, default);
            Check(File.ReadAllBytes(staged).SequenceEqual(payload[..4096]), "corrupt local startup cache is repaired");
            File.WriteAllBytes(startup, new byte[4096]);
            try { await VmRecipes.StageAsync(client.BaseAddress!, asset, Path.Combine(root, "changed-cache"), default); throw new Exception("changed startup asset accepted"); }
            catch (IOException) { checks++; }
            Check(!Directory.EnumerateFiles(Path.Combine(root, "changed-cache"), "*.tmp", SearchOption.AllDirectories).Any(), "failed asset download leaves no temporary file");
            foreach (var invalid in new[] { "{\"Media\":\"cd\",\"Floppy\":\"../outside.img\"}", "{\"Media\":\"disk\",\"Cpu\":\"host\"}", "{\"Media\":\"cd\",\"Arguments\":[\"-drive\"]}", "{\"Media\":\"cd\",\"Floppy\":\"missing.img\"}" })
            {
                File.WriteAllText(sidecar, invalid);
                configured = JsonSerializer.Deserialize<VmImage[]>(await client.GetStringAsync("/catalog.json"))!.Single(i => i.Path == "/isos/win98.iso");
                Check(configured.Problem is not null && configured.Boot is null, "invalid boot profile blocks only its image");
                Check(!VmClient.Html([configured], "test", null, 0).Contains("<button"), "unusable boot profile has no Start button");
            }
            foreach (var invalid in new[] { asset with { Path = "//outside.example/file" }, asset with { Path = "/files/%2e%2e%2fsecret" }, asset with { Size = 5L << 20 }, asset with { Sha256 = "../unsafe" } })
            {
                try { VmRecipes.Validate(new("cd", Floppy: invalid)); throw new Exception("bad remote recipe accepted"); }
                catch (ArgumentException) { checks++; }
            }
            File.Delete(sidecar);
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
