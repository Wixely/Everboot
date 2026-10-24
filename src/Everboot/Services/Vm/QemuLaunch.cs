using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Everboot.Services.Vm;

internal sealed record VmImage(string Name, string Path, long Size, string Profile, VmBootRecipe? Boot = null, string? Problem = null);

internal static class QemuLaunch
{
    internal static readonly string[] Profiles = ["legacy", "classic", "modern"];

    internal static string RecommendedProfile(IsoMetadata metadata) =>
        metadata.WindowsEra == WindowsBootEra.XpClass || metadata.Profile?.Id == "slitaz" ? "classic" :
        metadata.IsModernWindows || metadata.Profile?.Family is DistroFamily.LinuxLive or DistroFamily.LinuxInstaller ? "modern" : "legacy";

    internal static Uri MediaUri(Uri server, VmImage image)
    {
        if (server.Scheme is not ("http" or "https") || server.UserInfo.Length != 0)
            throw new ArgumentException("Use an HTTP or HTTPS server address without embedded credentials.");
        if (!image.Path.StartsWith("/isos/", StringComparison.Ordinal) || image.Path.Contains('?') || image.Path.Contains('#') ||
            image.Path[6..].Contains('/') || image.Path.Contains('\\'))
            throw new ArgumentException("Invalid image path in server catalog.");
        var uri = new Uri(server, image.Path);
        if (uri.Authority != server.Authority || !uri.AbsolutePath.StartsWith("/isos/", StringComparison.Ordinal))
            throw new ArgumentException("Image must belong to the selected server.");
        return uri;
    }

    internal static string DiskBlock(string disk, string? backing) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["driver"] = "qcow2",
        ["node-name"] = "disk",
        ["file"] = new { driver = "file", filename = disk },
        ["backing"] = backing
    });

    internal static ProcessStartInfo Command(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    internal static string FindExecutable(string name, string? directory)
    {
        var file = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        var dirs = new List<string?> { directory, System.IO.Path.Combine(AppContext.BaseDirectory, "qemu") };
        if (OperatingSystem.IsWindows()) dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator));
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var candidate = System.IO.Path.Combine(dir!, file);
            if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"QEMU is missing. Put a complete QEMU distribution in the qemu folder beside Everboot, install QEMU, or pass --qemu-dir <folder>. Required: {file}");
    }

    internal static async Task CheckRangeAsync(HttpClient http, Uri media, long expectedSize, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, media);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 511);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != 0 || range.To != 511 || range.Length != expectedSize)
            throw new IOException("The image changed or the server does not support byte-range streaming. Refresh the library and check the server.");
        // Never buffer a server's unexpected full-image response.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await stream.ReadExactlyAsync(new byte[512], ct);
    }

    internal static List<string> Arguments(string streamedBlock, string profile, string disk, bool iso, bool floppy, bool firstBoot = true)
    {
        var (ram, cpu) = profile switch
        {
            "legacy" => (128, "pentium2"),
            "classic" => (512, "pentium3"),
            "modern" => (2048, "qemu64"),
            _ => throw new ArgumentException("Unknown machine profile.")
        };
        var args = new List<string> { "-name", "Everboot", "-machine", "pc", "-accel", "tcg", "-cpu", cpu,
            "-m", ram.ToString(System.Globalization.CultureInfo.InvariantCulture), "-smp", "1", "-nic", "none", "-vga", "std", "-monitor", "none", "-serial", "none" };
        args.AddRange(["-blockdev", streamedBlock]);
        if (iso)
        {
            args.AddRange(["-device", "ide-cd,drive=media,bus=ide.1,unit=0"]);
        }
        args.AddRange(["-blockdev", DiskBlock(disk, iso ? null : "media"), "-device", floppy ? "floppy,drive=disk" : "ide-hd,drive=disk,bus=ide.0,unit=0", "-boot", iso ? firstBoot ? "order=cd,once=d,menu=on" : "order=cd,menu=on" : floppy ? "order=a,menu=on" : "order=c,menu=on"]);
        return args;
    }

    internal static async Task<Process> StartAsync(Uri server, VmImage image, string profile, string? qemuDir, string dataDir, HttpClient http, CancellationToken ct, int diagnosticPort = 0, IReadOnlyList<string>? diagnosticArguments = null, string? diagnosticLocalMedia = null)
    {
        var media = MediaUri(server, image);
        if (image.Problem is not null) throw new IOException(image.Problem);
        if (image.Boot is not null) VmRecipes.Validate(image.Boot);
        if (image.Size < 512 || !Profiles.Contains(profile)) throw new ArgumentException("Invalid image or machine profile.");
        var qemu = FindExecutable("qemu-system-x86_64", qemuDir);
        var img = FindExecutable("qemu-img", System.IO.Path.GetDirectoryName(qemu));
        await CheckRangeAsync(http, media, image.Size, ct);
        var bridge = new HttpBlockBridge(media, image.Size);
        try
        {
            // URL, size and profile form a stable local machine identity. No server-controlled file paths.
            var identity = media.AbsoluteUri + "\n" + image.Size + "\n" + profile;
            if (image.Boot is not null) identity += "\n" + JsonSerializer.Serialize(image.Boot);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            var folder = System.IO.Path.Combine(dataDir, key);
            Directory.CreateDirectory(folder);
            var disk = System.IO.Path.Combine(folder, "disk.qcow2");
            var iso = image.Boot is { } boot ? boot.Media == "cd" : media.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            var floppy = image.Boot is { } boot2 ? boot2.Media == "floppy" : !iso && image.Size <= 4 * 1024 * 1024;
            if (floppy && image.Size > 4 * 1024 * 1024) throw new ArgumentException("Floppy image is too large.");
            var firstBoot = !File.Exists(disk);
            // Fetch and verify small boot assets before creating the persistent machine.
            var staged = new Dictionary<string, string>();
            foreach (var (role, asset) in new[] { ("kernel", image.Boot?.Kernel), ("initrd", image.Boot?.Initrd), ("floppy", image.Boot?.Floppy) })
                if (asset is not null) staged[role] = await VmRecipes.StageAsync(server, asset, dataDir, ct);
            if (firstBoot)
            {
                var temp = disk + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var raw = temp + ".raw";
                try
                {
                    var create = new List<string> { "create", "-f", "qcow2" };
                    if (!iso) create.AddRange(["-u", "-F", "raw", "-b", "everboot-streamed-source"]);
                    create.Add(temp);
                    create.Add(iso ? profile == "legacy" ? "2G" : profile == "classic" ? "8G" : "32G" : image.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (image.Boot?.InitialDisk == "fat16")
                    {
                        DosInstallDisk.Create(raw);
                        create = ["convert", "-f", "raw", "-O", "qcow2", raw, temp];
                    }
                    var info = Command(img, create);
                    info.RedirectStandardError = true; info.RedirectStandardOutput = true; info.CreateNoWindow = true;
                    using var process = Process.Start(info) ?? throw new IOException("Could not start qemu-img.");
                    var error = process.StandardError.ReadToEndAsync(ct);
                    var output = process.StandardOutput.ReadToEndAsync(ct);
                    try { await process.WaitForExitAsync(ct); }
                    catch { if (!process.HasExited) process.Kill(true); throw; }
                    await output;
                    if (process.ExitCode != 0) throw new IOException($"Could not create VM disk (exit {process.ExitCode}). Check the QEMU installation and writable VM data folder. " + await error);
                    File.Move(temp, disk, false);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                    if (File.Exists(raw)) File.Delete(raw);
                }
            }
            var mediaBlock = diagnosticPort > 0 && diagnosticLocalMedia is not null
                ? JsonSerializer.Serialize(new Dictionary<string, object> { ["driver"] = "raw", ["node-name"] = "media", ["read-only"] = true,
                    ["file"] = new { driver = "file", filename = Path.GetFullPath(diagnosticLocalMedia) } }) : bridge.Block;
            var arguments = Arguments(mediaBlock, profile, disk, iso, floppy, firstBoot);
            if (image.Boot?.Cpu == "intel-max") arguments[arguments.IndexOf("-cpu") + 1] = "max,vendor=GenuineIntel,vmx=off,-x2apic";
            if (staged.TryGetValue("kernel", out var kernel)) arguments.AddRange(["-kernel", kernel]);
            if (staged.TryGetValue("initrd", out var initrd)) arguments.AddRange(["-initrd", initrd]);
            if (image.Boot?.CommandLine is { } commandLine) arguments.AddRange(["-append", commandLine]);
            if (staged.TryGetValue("floppy", out var startupFloppy))
            {
                arguments.AddRange(["-blockdev", JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["driver"] = "raw", ["node-name"] = "startup", ["read-only"] = true,
                    ["file"] = new { driver = "file", filename = startupFloppy }
                }), "-device", "floppy,drive=startup"]);
                arguments[arguments.IndexOf("-boot") + 1] = firstBoot ? "order=cda,once=a,menu=on" : "order=cda,menu=on";
            }
            var start = Command(qemu, arguments);
            start.RedirectStandardError = true;
            if (diagnosticPort > 0)
            {
                start.ArgumentList.Add("-display"); start.ArgumentList.Add("none");
                start.ArgumentList.Add("-qmp"); start.ArgumentList.Add($"tcp:127.0.0.1:{diagnosticPort},server=on,wait=off");
                // Only the local test harness supplies these. Never accepted by the client web UI or server catalog.
                foreach (var argument in diagnosticArguments ?? []) start.ArgumentList.Add(argument);
            }
            var guest = Process.Start(start) ?? throw new IOException("Could not start QEMU.");
            guest.EnableRaisingEvents = true;
            guest.Exited += (_, _) => bridge.Dispose();
            if (guest.HasExited) bridge.Dispose();
            return guest;
        }
        catch { bridge.Dispose(); throw; }
    }
}
