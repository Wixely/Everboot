using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Everboot.Configuration;
using Everboot.Services;
using Everboot.Services.Nfs;
using Everboot.Services.Smb;
using Everboot.Services.Vm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.FirstOrDefault() == "--inspect")
{
    if (args.Length != 2) throw new ArgumentException("Usage: --inspect <image-directory>");
    var inspector = new IsoInspector(NullLogger<IsoInspector>.Instance);
    foreach (var path in Directory.EnumerateFiles(args[1], "*.iso"))
    {
        var metadata = inspector.Inspect(path);
        Console.WriteLine(JsonSerializer.Serialize(new { File = Path.GetFileName(path), Size = new FileInfo(path).Length,
            metadata.VolumeLabel, Era = metadata.WindowsEra.ToString(), Profile = metadata.Profile?.Id,
            metadata.Squashfs, Recipes = metadata.AvailableRecipes?.Select(r => r.Id) }));
    }
    return 0;
}
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + name);
}

// Real QEMU smoke test. Optional supplied ISO enables interactive guest validation.
// Commands below control only the disposable guest. No shell evaluation.
var explicitServerData = Option("--server-data");
var root = explicitServerData is null ? Path.Combine(Path.GetTempPath(), "everboot-vm-probe-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(explicitServerData);
if (Directory.Exists(root)) throw new IOException("Probe server directory must be new.");
Directory.CreateDirectory(Path.Combine(root, "isos"));
Console.WriteLine("Probe server data: " + root);
var source = args.FirstOrDefault();
var file = source is null || args.Contains("--raw") ? "disk.img" : "boot.iso";
if (source is null)
{
    using var disk = File.Create(Path.Combine(root, "isos", file)); disk.SetLength(16 * 1024 * 1024);
}
else File.Copy(source, Path.Combine(root, "isos", file));
if (Option("--recipe") is { } recipePath) File.Copy(recipePath, Path.Combine(root, "isos", file + ".vm.json"));
if (Option("--support-dir") is { } supportDirectory)
{
    Directory.CreateDirectory(Path.Combine(root, "tftp"));
    foreach (var assetPath in Directory.EnumerateFiles(supportDirectory))
        File.Copy(assetPath, Path.Combine(root, "tftp", Path.GetFileName(assetPath)));
}
var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
if (Option("--port") is { } portText) port = int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture);
var machineData = Option("--machine-data") is { } machinePath ? Path.GetFullPath(machinePath) : Path.Combine(root, "machines");
var config = new EverbootOptions { DataDirectory = root, CatalogRescanIntervalSeconds = 0 };
config.Http.BindAddress = "localhost"; config.Http.Port = port;
var options = Options.Create(config);
using var catalog = new BootCatalog(options, new IsoInspector(NullLogger<IsoInspector>.Instance), NullLogger<BootCatalog>.Instance);
using var server = new HttpFileService(NullLogger<HttpFileService>.Instance, catalog, new BootConfigGenerator(catalog, options), options, new NfsStatus());
using var http = new HttpClient();
Process? qemu = null;
try
{
    await server.StartAsync(default);
    var uri = new Uri($"http://localhost:{port}");
    var metadata = catalog.Entries.Single().Metadata;
    if (args.Contains("--verify-wim"))
    {
        if (!metadata.IsModernWindows) throw new Exception("Windows installer was not detected.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/iso/boot/sources/boot.wim"));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 7);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != HttpStatusCode.PartialContent) throw new Exception("WIM range request failed.");
        var bytes = new byte[8];
        using (var body = await response.Content.ReadAsStreamAsync()) await body.ReadExactlyAsync(bytes);
        if (!bytes.AsSpan(0, 5).SequenceEqual("MSWIM"u8)) throw new Exception("HTTP did not serve the WIM header.");
        using var export = new NfsExport(catalog.Entries.Single());
        var sources = export.Children(export.Root).Single(n => n.Name.Equals("sources", StringComparison.OrdinalIgnoreCase));
        var wim = export.Children(sources).Single(n => n.Name.Equals("boot.wim", StringComparison.OrdinalIgnoreCase));
        if (!export.Read(wim, 0, 8).SequenceEqual(bytes)) throw new Exception("NFS export WIM differs.");
        var shares = new SmbShareCatalog(catalog, options, NullLogger<SmbShareCatalog>.Instance);
        if (!shares.TryOpen("boot", out var share)) throw new Exception("SMB share did not open.");
        using (share)
        using (var content = share!.FileSystem.OpenFile(IsoFileResolver.Resolve(share.FileSystem, "sources/boot.wim")!, FileMode.Open, FileAccess.Read))
        {
            var smbBytes = new byte[8]; content.ReadExactly(smbBytes);
            if (!smbBytes.SequenceEqual(bytes)) throw new Exception("SMB share WIM differs.");
        }
        Console.WriteLine("PASS: Windows detection, HTTP WIM range, NFS export and SMB share agree on the WIM header.");
        return 0;
    }
    var image = VmRecipes.Describe(catalog.Entries.Single(), catalog.TftpDirectory);
    var profile = Option("--profile") ?? image.Profile;
    var qmpPort = new TcpListener(IPAddress.Loopback, 0); qmpPort.Start();
    var controlPort = ((IPEndPoint)qmpPort.LocalEndpoint).Port; qmpPort.Stop();
    var diagnosticArgs = Option("--qemu-args") is { } argsPath ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(argsPath)) : null;
    qemu = await QemuLaunch.StartAsync(uri, image, profile, null, machineData, http, default,
        diagnosticPort: controlPort, diagnosticArguments: diagnosticArgs,
        diagnosticLocalMedia: args.Contains("--local-media") ? source : null);
    var errors = qemu.StandardError.ReadToEndAsync();
    using var control = new TcpClient();
    for (var i = 0; !control.Connected; i++)
    {
        try { await control.ConnectAsync(IPAddress.Loopback, controlPort); }
        catch (SocketException) when (i < 100 && !qemu.HasExited) { await Task.Delay(100); }
        catch (SocketException) { throw new IOException("QEMU control connection failed: " + await errors); }
    }
    using var input = new StreamReader(control.GetStream());
    using var outputControl = new StreamWriter(control.GetStream(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    var greeting = await input.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
    if (greeting is null) throw new Exception("QEMU failed: " + await errors);
    await Qmp("qmp_capabilities");
    Console.WriteLine("PASS: QEMU opened " + (args.Contains("--local-media") ? "LOCAL CONTROL media" : "streamed media") + ". Profile: " + profile + ". HTTP: " + uri);
    Console.WriteLine("PASS: " + await Qmp("query-block"));
    if (source is not null)
    {
        Console.WriteLine("Interactive boot probe: shot <path.png|ppm>, key <qcode[+qcode]>, text <lowercase text>, wait <seconds>, stats [path.json], powerdown, reset, quit");
        using var script = Option("--script") is { } scriptPath ? File.OpenText(scriptPath) : null;
        var commands = (TextReader?)script ?? Console.In;
        while (commands.ReadLine() is { } command && command != "quit")
        {
            var parts = command.Split(' ', 2);
            if (parts[0] == "shot" && parts.Length == 2) Console.WriteLine(await Qmp("screendump", new { filename = Path.GetFullPath(parts[1]), format = Path.GetExtension(parts[1]) == ".png" ? "png" : "ppm" }));
            else if (parts[0] == "key" && parts.Length == 2)
            {
                Console.WriteLine(await Qmp("send-key", new { keys = parts[1].Split('+').Select(k => new { type = "qcode", data = k }).ToArray() }));
                await Task.Delay(150); // QMP replies before key release; avoid dropping the next key.
            }
            else if (parts[0] == "stats")
            {
                var stats = await Qmp("query-blockstats");
                if (parts.Length == 2) File.WriteAllText(parts[1], stats);
                else Console.WriteLine(stats);
            }
            else if (parts[0] == "reset") Console.WriteLine(await Qmp("system_reset"));
            else if (parts[0] == "powerdown") Console.WriteLine(await Qmp("system_powerdown"));
            else if (parts[0] == "text" && parts.Length == 2)
            {
                foreach (var c in parts[1])
                {
                    if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '-'))
                        throw new ArgumentException("Probe text supports lowercase letters, digits, spaces and hyphens.");
                    await Qmp("send-key", new { keys = new[] { new { type = "qcode", data = c == ' ' ? "spc" : c == '-' ? "minus" : c.ToString() } } });
                    await Task.Delay(150);
                }
            }
            else if (parts[0] == "wait" && parts.Length == 2 && int.TryParse(parts[1], out var seconds) && seconds is >= 0 and <= 300)
                await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
    }
    try { await Qmp("quit"); }
    catch (IOException) { } // Some QEMU builds close QMP before returning the quit reply.
    await qemu.WaitForExitAsync();
    if (qemu.ExitCode != 0) throw new Exception(await errors);
    if (source is null)
    {
        var overlay = Directory.GetFiles(machineData, "disk.qcow2", SearchOption.AllDirectories).Single();
        using var reopenBridge = new HttpBlockBridge(new Uri(uri, image.Path), image.Size);
        var rebaseInfo = QemuLaunch.Command(QemuLaunch.FindExecutable("qemu-img", null), ["rebase", "-u", "-F", "raw", "-b", $"nbd://127.0.0.1:{reopenBridge.Port}", overlay]);
        using (var rebase = Process.Start(rebaseInfo)!) { await rebase.WaitForExitAsync(); if (rebase.ExitCode != 0) throw new IOException("Test backing update failed."); }
        var diskJson = overlay;
        var writeInfo = QemuLaunch.Command(QemuLaunch.FindExecutable("qemu-io", null), ["-c", "write -P 0x5a 1048576 512", diskJson]);
        writeInfo.RedirectStandardError = true; writeInfo.RedirectStandardOutput = true;
        using (var write = Process.Start(writeInfo)!)
        {
            var writeOutput = write.StandardOutput.ReadToEndAsync(); var writeError = write.StandardError.ReadToEndAsync();
            await write.WaitForExitAsync();
            if (write.ExitCode != 0) throw new IOException(await writeOutput + await writeError);
        }
        var info = QemuLaunch.Command(QemuLaunch.FindExecutable("qemu-io", null), ["-c", "read -P 0x5a 1048576 512", diskJson]);
        info.RedirectStandardError = true; info.RedirectStandardOutput = true;
        using var verify = Process.Start(info)!;
        var output = verify.StandardOutput.ReadToEndAsync(); var error = verify.StandardError.ReadToEndAsync();
        await verify.WaitForExitAsync();
        if (verify.ExitCode != 0) throw new IOException(await output + await error);
        using var original = File.OpenRead(Path.Combine(root, "isos", file)); original.Position = 1048576;
        var sector = new byte[512]; original.ReadExactly(sector);
        if (sector.Any(b => b != 0)) throw new IOException("Source image was modified.");
        Console.WriteLine("PASS: overlay write survives reopening; source sector is unchanged.");
    }
    Console.WriteLine("PASS: QEMU shut down cleanly.");
    return 0;

    async Task<string> Qmp(string command, object? arguments = null)
    {
        await outputControl.WriteLineAsync(JsonSerializer.Serialize(new { execute = command, arguments = arguments ?? new { } }));
        while (true)
        {
            var line = await input.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new IOException("QMP closed.");
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.ToString());
            if (json.RootElement.TryGetProperty("return", out _)) return line;
        }
    }
}
finally
{
    if (qemu is not null) { if (!qemu.HasExited) qemu.Kill(true); qemu.Dispose(); }
    await server.StopAsync(default); catalog.Dispose();
    if (explicitServerData is null) Directory.Delete(root, true); // Only this probe's generated temporary tree.
}
