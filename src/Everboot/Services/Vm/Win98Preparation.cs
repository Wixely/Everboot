using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everboot.Services.Vm;

internal static class Win98Preparation
{
    internal static int Run(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: Everboot --prepare-win98 <image.iso> <server-data/tftp>");
            return 2;
        }
        try
        {
            var profile = Prepare(args[1], args[2]);
            Console.WriteLine("Prepared startup support from this CD. Refresh the VM library and click Start.");
            Console.WriteLine("Profile: " + profile);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine("Could not prepare this image: " + ex.Message);
            return 1;
        }
    }

    internal static string Prepare(string image, string supportDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Preparation currently requires Windows; generated startup support can also be served from Linux.");
        image = Path.GetFullPath(image);
        supportDirectory = Path.GetFullPath(supportDirectory);
        var sidecar = image + ".vm.json";
        if (File.Exists(sidecar)) throw new IOException("A boot profile already exists. Preserve or move it before preparing this image.");
        var metadata = new IsoInspector(NullLogger<IsoInspector>.Instance).Inspect(image);
        using var source = File.OpenRead(image);
        using var fs = IsoFileSystem.Open(source, metadata.Layout) ?? throw new IOException("An ISO filesystem is required.");
        if (metadata.WindowsEra != WindowsBootEra.NinexClass || IsoFileResolver.Resolve(fs, "win98/setup.exe") is null)
            throw new IOException("This preparation supports Windows 98 CDs with a WIN98 setup directory.");
        // Only the measured cabinet layout is supported. Other editions fail clearly.
        string[] cabinets = ["BASE5.CAB", "WIN98_25.CAB"];
        string[] names = ["WINBOOT.SYS", "COMMAND.COM", "HIMEM.SYS", "OAKCDROM.SYS", "MSCDEX.EXE", "FDISK.EXE", "FORMAT.COM", "SYS.COM"];
        var staging = Path.Combine(Path.GetTempPath(), "everboot-dos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var cabinet in cabinets)
            {
                var path = IsoFileResolver.Resolve(fs, "win98/" + cabinet) ?? throw new IOException("Required startup cabinet is missing: " + cabinet);
                using var content = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
                if (content.Length is <= 0 or > 8 * 1024 * 1024) throw new IOException("Unsupported startup cabinet size.");
                using var output = File.Create(Path.Combine(staging, cabinet));
                content.CopyTo(output, 65536);
            }
            var extracted = Path.Combine(staging, "files"); Directory.CreateDirectory(extracted);
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (var cabinet in cabinets) WindowsCabinet.Extract(Path.Combine(staging, cabinet), extracted, wanted);
            var files = new Dictionary<string, byte[]>();
            foreach (var name in names)
            {
                var path = Path.Combine(extracted, name);
                if (!File.Exists(path)) throw new IOException("Required startup component is missing: " + name);
                files.Add(name == "WINBOOT.SYS" ? "IO.SYS" : name, File.ReadAllBytes(path));
            }
            files.Add("MSDOS.SYS", Text("[Options]\nBootGUI=0\nLogo=0\n"));
            files.Add("CONFIG.SYS", Text("DEVICE=A:\\HIMEM.SYS /TESTMEM:OFF\nDEVICE=A:\\OAKCDROM.SYS /D:EVBCD\nFILES=40\nBUFFERS=20\nLASTDRIVE=Z\n"));
            files.Add("AUTOEXEC.BAT", Text("@ECHO OFF\nA:\\MSCDEX.EXE /D:EVBCD /L:R\nIF ERRORLEVEL 1 GOTO FAILED\nECHO Everboot: starting Setup from your streamed CD.\nR:\\WIN98\\SETUP.EXE\nGOTO END\n:FAILED\nECHO Everboot: CD unavailable. Check the server and restart.\n:END\nECHO Disk tools: A:\\FDISK.EXE and A:\\FORMAT.COM\n"));
            var disk = DosStartupImage.Build(files);
            var assetName = "everboot-dos98-" + Convert.ToHexString(SHA256.HashData(disk))[..24] + ".img";
            Directory.CreateDirectory(supportDirectory);
            var asset = Path.Combine(supportDirectory, assetName);
            if (File.Exists(asset))
            {
                if (new FileInfo(asset).Length != disk.Length || !File.ReadAllBytes(asset).AsSpan().SequenceEqual(disk))
                    throw new IOException("Existing startup asset does not match its expected content.");
            }
            else
            {
                WriteNew(asset, disk);
            }
            WriteNew(sidecar, JsonSerializer.SerializeToUtf8Bytes(new VmRecipeFile("cd", "legacy", Floppy: assetName, InitialDisk: "fat16"),
                new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            return sidecar;
        }
        finally { Directory.Delete(staging, true); } // Only this call's unique private staging directory.
    }

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n"));

    private static void WriteNew(string target, byte[] content)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(content);
                file.Flush(true);
            }
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
