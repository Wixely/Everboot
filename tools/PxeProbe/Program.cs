using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Everboot.Configuration;
using Everboot.Services;
using Everboot.Services.Nfs;
using Everboot.Services.Smb;
using Microsoft.Extensions.Logging;
using Everboot.Services.Vm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// QEMU is ONLY a PXE client here: no CD, disk, -kernel, or NBD is attached.
// Its isolated user-network DHCP stands in for the LAN's address server and
// points NIC iPXE firmware at Everboot's real generated menu. ProxyDHCP/TFTP
// broadcast discovery is a separate acceptance test.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: PxeProbe <image.iso> <new-artifact-directory> [--ram MiB] [--port port] [--qemu-dir directory] [--wimboot file] [--smb-debug]");
    return 2;
}
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + name);
}
var source = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new IOException("Use a new artifact directory; results are retained.");
var ram = int.Parse(Option("--ram") ?? "1024");
var port = int.Parse(Option("--port") ?? "38184");
if (ram is < 128 or > 8192 || port is < 1024 or > 65530) throw new ArgumentException("Invalid RAM or port.");
Directory.CreateDirectory(Path.Combine(root, "isos"));
File.Copy(source, Path.Combine(root, "isos", "live.iso"));
var config = new EverbootOptions { DataDirectory = root, CatalogRescanIntervalSeconds = 0 };
config.Http.BindAddress = "+"; config.Http.Port = port;
config.Dhcp.Enabled = false; config.Dhcp.ServerAddress = "10.0.2.2";
config.Nfs.BindAddress = "127.0.0.1";
config.Nfs.Port = port + 1; config.Nfs.MountPort = port + 2; config.Nfs.PortmapperPort = 111;
config.Iscsi.Enabled = false; config.Smb.Enabled = Option("--wimboot") is not null;
config.Smb.BindAddress = "127.0.0.1";
if (Option("--wimboot") is { } loader)
{
    Directory.CreateDirectory(Path.Combine(root, "tftp"));
    File.Copy(loader, Path.Combine(root, "tftp", "wimboot"));
}
using var logs = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(args.Contains("--smb-debug") ? LogLevel.Debug : LogLevel.Warning));
var options = Options.Create(config);
using var catalog = new BootCatalog(options, new IsoInspector(NullLogger<IsoInspector>.Instance), NullLogger<BootCatalog>.Instance);
var status = new NfsStatus();
using var http = new HttpFileService(NullLogger<HttpFileService>.Instance, catalog, new BootConfigGenerator(catalog, options), options, status);
using var nfs = new NfsService(catalog, options, status, NullLogger<NfsService>.Instance);
var shares = new SmbShareCatalog(catalog, options, logs.CreateLogger<SmbShareCatalog>());
using var smb = new SmbService(logs.CreateLogger<SmbService>(), shares, options);
Process? qemu = null;
try
{
    await http.StartAsync(default); await nfs.StartAsync(default); await smb.StartAsync(default);
    // BackgroundService.StartAsync can return before binding. Surface failures
    // before launching a guest rather than leaving it waiting on a dead server.
    using (var ready = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
    {
        for (var attempt = 0; ; attempt++)
        {
            if (http.ExecuteTask?.IsFaulted == true) await http.ExecuteTask;
            if (nfs.ExecuteTask?.IsFaulted == true) await nfs.ExecuteTask;
            if (smb.ExecuteTask?.IsFaulted == true) await smb.ExecuteTask;
            try { (await ready.GetAsync($"http://localhost:{port}/boot.ipxe")).EnsureSuccessStatusCode(); if (status.Running) break; }
            catch (HttpRequestException) when (attempt < 20) { }
            if (attempt >= 20) throw new IOException("PXE services did not become ready; check HTTP permissions and portmapper TCP 111 permissions/conflicts.");
            await Task.Delay(100);
        }
    }
    if (config.Smb.Enabled)
    {
        using var smbReady = new TcpClient();
        await smbReady.ConnectAsync(IPAddress.Loopback, config.Smb.Port);
        if (smb.ExecuteTask?.IsFaulted == true) await smb.ExecuteTask;
    }
    var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
    var qmpPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    var executable = QemuLaunch.FindExecutable("qemu-system-x86_64", Option("--qemu-dir"));
    string[] qemuArgs = ["-name", "Everboot PXE test", "-machine", "pc", "-accel", "tcg", "-cpu", "max", "-m", ram.ToString(), "-smp", "2",
        "-boot", "order=n,menu=on", "-nic", $"user,model=e1000,bootfile=http://10.0.2.2:{port}/boot.ipxe",
        "-display", "none", "-monitor", "none", "-serial", "file:" + Path.Combine(root, "serial.txt"),
        "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off"];
    File.WriteAllText(Path.Combine(root, "qemu-arguments.json"), JsonSerializer.Serialize(qemuArgs, new JsonSerializerOptions { WriteIndented = true }));
    var start = QemuLaunch.Command(executable, qemuArgs); start.RedirectStandardError = true;
    qemu = Process.Start(start) ?? throw new IOException("Could not start QEMU.");
    var errors = qemu.StandardError.ReadToEndAsync();
    using var control = new TcpClient();
    for (var i = 0; !control.Connected; i++)
    {
        try { await control.ConnectAsync(IPAddress.Loopback, qmpPort); }
        catch (SocketException) when (i < 100 && !qemu.HasExited) { await Task.Delay(100); }
        catch (SocketException) { throw new IOException("QEMU failed: " + await errors); }
    }
    using var reader = new StreamReader(control.GetStream());
    using var writer = new StreamWriter(control.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
    await Qmp("qmp_capabilities");
    Console.WriteLine($"PXE client: {ram} MiB RAM, no attached media. HTTP {port}; NFS {port + 1}; mount {port + 2}.");
    Console.WriteLine("Commands: shot <name.png>, key <qcode[+qcode]>, text <ASCII>, stats, wait <seconds>, powerdown, quit");
    while (Console.ReadLine() is { } line && line != "quit")
    {
        var parts = line.Split(' ', 2);
        if (parts[0] == "shot" && parts.Length == 2)
            Console.WriteLine(await Qmp("screendump", new { filename = Path.GetFullPath(Path.Combine(root, parts[1])), format = "png" }));
        else if (parts[0] == "key" && parts.Length == 2) await Key(parts[1]);
        else if (parts[0] == "text" && parts.Length == 2)
        {
            foreach (var c in parts[1])
            {
                var key = c switch
                {
                    >= 'a' and <= 'z' or >= '0' and <= '9' => c.ToString(),
                    >= 'A' and <= 'Z' => "shift+" + char.ToLowerInvariant(c),
                    ' ' => "spc", '/' => "slash", '-' => "minus", '_' => "shift+minus", '.' => "dot", ',' => "comma",
                    ':' => "shift+semicolon", ';' => "semicolon", '=' => "equal", '\\' => "backslash", '|' => "shift+backslash",
                    '\'' => "apostrophe", '"' => "shift+apostrophe", '>' => "shift+dot", '<' => "shift+comma",
                    '(' => "shift+9", ')' => "shift+0", '$' => "shift+4", '*' => "shift+8", '&' => "shift+7",
                    _ => throw new ArgumentException("Unsupported probe character.")
                };
                await Key(key);
            }
        }
        else if (parts[0] == "stats")
        {
            var snapshot = new { status.Running, status.Reads, status.BytesRead, SmbBytesRead = shares.BytesRead, GuestRamMiB = ram, IsoBytes = new FileInfo(source).Length };
            var json = JsonSerializer.Serialize(snapshot);
            File.AppendAllText(Path.Combine(root, "nfs-samples.jsonl"), json + Environment.NewLine);
            Console.WriteLine(json);
        }
        else if (parts[0] == "wait" && parts.Length == 2 && int.TryParse(parts[1], out var seconds) && seconds is >= 0 and <= 60) await Task.Delay(seconds * 1000);
        else if (parts[0] == "powerdown") Console.WriteLine(await Qmp("system_powerdown"));
    }
    try { await Qmp("quit"); } catch (IOException) { }
    await qemu.WaitForExitAsync();
    File.WriteAllText(Path.Combine(root, "qemu-errors.txt"), await errors);
    if (qemu.ExitCode != 0) throw new IOException("QEMU exited unsuccessfully; see qemu-errors.txt.");
    return 0;

    async Task Key(string key)
    {
        await Qmp("send-key", new { keys = key.Split('+').Select(k => new { type = "qcode", data = k }).ToArray() });
        await Task.Delay(150);
    }
    async Task<string> Qmp(string command, object? arguments = null)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { execute = command, arguments = arguments ?? new { } }));
        while (true)
        {
            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new IOException("QMP closed.");
            using var json = JsonDocument.Parse(response);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.ToString());
            if (json.RootElement.TryGetProperty("return", out _)) return response;
        }
    }
}
finally
{
    if (qemu is not null) { if (!qemu.HasExited) qemu.Kill(true); qemu.Dispose(); }
    await smb.StopAsync(default); await nfs.StopAsync(default); await http.StopAsync(default);
}
