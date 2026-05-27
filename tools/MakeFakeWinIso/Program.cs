// Generates a tiny ISO/IMG that mimics one of several install media shapes,
// just enough that Everboot's IsoInspector classifies it correctly.
//
// usage: dotnet run --project tools/MakeFakeWinIso -- <output> [mode]
//   mode = modern | dual | xp | win98 | linux | floppy
//        | ubuntu | debian-netinst | fedora | arch | clonezilla | dban | ventoy
//
//   modern         - Vista+ BIOS chain only (bootmgr + boot.wim)
//   dual           - Vista+ BIOS + EFI chains both present
//   xp             - i386\\setupldr.bin layout (XP / 2000 / 2003)
//   win98          - WIN98\\setup.exe layout
//   linux          - just a readme.txt (falls through to sanboot)
//   floppy         - tiny .img (1.44 MB) so the catalog tags it as floppy
//   ubuntu         - casper layout + Ubuntu volume label
//   debian-netinst - install.amd layout + Debian volume label
//   fedora         - LiveOS + Fedora label
//   arch           - arch/boot layout + ARCH label
//   clonezilla     - live/ layout + Clonezilla label
//   dban           - DBAN markers (unmaintained warning)
//   ventoy         - Ventoy markers (not-for-PXE warning)

using System;
using System.IO;
using System.Text;
using DiscUtils.Iso9660;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: MakeFakeWinIso <output> [modern|dual|xp|win98|linux|floppy]");
    return 1;
}

var output = args[0];
var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "modern";

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

if (mode == "floppy")
{
    // 1.44 MB raw bytes - no filesystem structure; Everboot treats it as a raw
    // image, sees the .img extension and small size, tags it as floppy.
    var bytes = new byte[1474560];
    new Random(42).NextBytes(bytes);
    File.WriteAllBytes(output, bytes);
    Console.WriteLine($"Wrote {bytes.Length:N0} bytes to {output} (floppy)");
    return 0;
}

// Volume identifier - we match per profile against this when possible. Some
// modes override below.
var volumeId = mode switch
{
    "ubuntu" => "Ubuntu 24.04 LTS amd64",
    "debian-netinst" => "Debian 12.5",
    "fedora" => "Fedora-Workstation-Live-40",
    "arch" => "ARCH_202405",
    "clonezilla" => "Clonezilla-Live",
    "dban" => "DBAN 2.3.0",
    "ventoy" => "Ventoy",
    _ => mode.ToUpperInvariant(),
};

var builder = new CDBuilder
{
    UseJoliet = true,
    VolumeIdentifier = volumeId,
};

builder.AddFile("readme.txt", Encoding.UTF8.GetBytes($"fake {mode} iso for testing\n"));

switch (mode)
{
    case "modern":
        builder.AddFile("bootmgr", Filler("BOOTMGR", 4096));
        builder.AddFile("boot\\BCD", Filler("BCD", 8192));
        builder.AddFile("boot\\boot.sdi", Filler("BOOTSDI", 16 * 1024));
        builder.AddFile("sources\\boot.wim", Filler("BOOTWIM", 256 * 1024));
        break;

    case "dual":
        builder.AddFile("bootmgr", Filler("BOOTMGR", 4096));
        builder.AddFile("boot\\BCD", Filler("BCD", 8192));
        builder.AddFile("boot\\boot.sdi", Filler("BOOTSDI", 16 * 1024));
        builder.AddFile("sources\\boot.wim", Filler("BOOTWIM", 256 * 1024));
        builder.AddFile("efi\\microsoft\\boot\\BCD", Filler("EFIBCD", 8192));
        builder.AddFile("efi\\boot\\bootx64.efi", Filler("BOOTX64", 32 * 1024));
        builder.AddFile("efi\\microsoft\\boot\\bootmgfw.efi", Filler("BOOTMGFW", 32 * 1024));
        break;

    case "xp":
        builder.AddFile("I386\\SETUPLDR.BIN", Filler("XPLDR", 32 * 1024));
        builder.AddFile("I386\\NTDETECT.COM", Filler("NTDETECT", 32 * 1024));
        builder.AddFile("I386\\TXTSETUP.SIF", Filler("TXTSETUP", 64 * 1024));
        break;

    case "win98":
        builder.AddFile("WIN98\\SETUP.EXE", Filler("SETUPWIN98", 64 * 1024));
        builder.AddFile("WIN98\\BASE2.CAB", Filler("BASE2", 128 * 1024));
        builder.AddFile("WIN98\\SETUP.TXT", Encoding.UTF8.GetBytes("Windows 98 SE Setup\n"));
        break;

    case "ubuntu":
        builder.AddFile("casper\\vmlinuz", Filler("VMLINUZ", 64 * 1024));
        builder.AddFile("casper\\initrd", Filler("INITRD", 64 * 1024));
        builder.AddFile("casper\\filesystem.squashfs", Filler("SQUASHFS", 128 * 1024));
        break;

    case "debian-netinst":
        builder.AddFile("install.amd\\vmlinuz", Filler("DEBVMLINUZ", 64 * 1024));
        builder.AddFile("install.amd\\initrd.gz", Filler("DEBINITRD", 64 * 1024));
        break;

    case "fedora":
        builder.AddFile("LiveOS\\squashfs.img", Filler("FEDSQUASH", 128 * 1024));
        builder.AddFile("isolinux\\vmlinuz", Filler("FEDVMLINUZ", 64 * 1024));
        break;

    case "arch":
        builder.AddFile("arch\\boot\\x86_64\\vmlinuz-linux", Filler("ARCHKERNEL", 64 * 1024));
        builder.AddFile("arch\\boot\\x86_64\\initramfs-linux.img", Filler("ARCHINITRAMFS", 64 * 1024));
        break;

    case "clonezilla":
        builder.AddFile("live\\vmlinuz", Filler("CLONEZVM", 64 * 1024));
        builder.AddFile("live\\initrd.img", Filler("CLONEZINITRD", 64 * 1024));
        break;

    case "dban":
        builder.AddFile("isolinux\\dban.bzi", Filler("DBAN", 64 * 1024));
        break;

    case "ventoy":
        builder.AddFile("ventoy\\ventoy.json", Encoding.UTF8.GetBytes("{}\n"));
        builder.AddFile("ventoy\\ventoy.cpio", Filler("VENTOYCPIO", 32 * 1024));
        break;

    case "linux":
    default:
        // nothing extra - just readme.txt - falls through to sanboot
        break;
}

builder.Build(output);
Console.WriteLine($"Wrote {new FileInfo(output).Length:N0} bytes to {output} ({mode})");
return 0;

static byte[] Filler(string tag, int size)
{
    var bytes = new byte[size];
    var pattern = Encoding.ASCII.GetBytes(tag.PadRight(16));
    for (var i = 0; i < bytes.Length; i++)
    {
        bytes[i] = pattern[i % pattern.Length];
    }
    return bytes;
}
