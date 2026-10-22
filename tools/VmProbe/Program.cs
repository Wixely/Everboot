using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Everboot.Configuration;
using Everboot.Services;
using Everboot.Services.Nfs;
using Everboot.Services.Vm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Real QEMU smoke test. Optional supplied ISO enables interactive guest validation.
// Commands: shot <absolute-path.ppm>, key <qcode>, stats, quit. No shell evaluation.
var root = Path.Combine(Path.GetTempPath(), "everboot-vm-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "isos"));
var source = args.FirstOrDefault();
var file = source is null ? "disk.img" : "boot.iso";
if (source is null)
{
    using var disk = File.Create(Path.Combine(root, "isos", file)); disk.SetLength(16 * 1024 * 1024);
}
else File.Copy(source, Path.Combine(root, "isos", file));
var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
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
    var image = new VmImage(file, "/isos/" + file, new FileInfo(Path.Combine(root, "isos", file)).Length, "legacy");
    var qmpPort = new TcpListener(IPAddress.Loopback, 0); qmpPort.Start();
    var controlPort = ((IPEndPoint)qmpPort.LocalEndpoint).Port; qmpPort.Stop();
    qemu = await QemuLaunch.StartAsync(uri, image, "legacy", null, Path.Combine(root, "machines"), http, default, diagnosticPort: controlPort);
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
    Console.WriteLine("PASS: QEMU opened streamed media with 128 MiB guest RAM. HTTP: " + uri);
    Console.WriteLine("PASS: " + await Qmp("query-block"));
    if (source is not null)
    {
        Console.WriteLine("Interactive boot probe: shot <absolute-path.ppm>, key <qcode>, stats, quit");
        while (Console.ReadLine() is { } command && command != "quit")
        {
            var parts = command.Split(' ', 2);
            if (parts[0] == "shot" && parts.Length == 2) Console.WriteLine(await Qmp("screendump", new { filename = parts[1] }));
            else if (parts[0] == "key" && parts.Length == 2)
            {
                Console.WriteLine(await Qmp("send-key", new { keys = new[] { new { type = "qcode", data = parts[1] } } }));
                await Task.Delay(150); // QMP replies before key release; avoid dropping the next key.
            }
            else if (parts[0] == "stats") Console.WriteLine(await Qmp("query-blockstats"));
        }
    }
    try { await Qmp("quit"); }
    catch (IOException) { } // Some QEMU builds close QMP before returning the quit reply.
    await qemu.WaitForExitAsync();
    if (qemu.ExitCode != 0) throw new Exception(await errors);
    if (source is null)
    {
        var overlay = Directory.GetFiles(Path.Combine(root, "machines"), "disk.qcow2", SearchOption.AllDirectories).Single();
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
    Directory.Delete(root, true); // Only this probe's generated temporary tree.
}
