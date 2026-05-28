using System;
using System.IO;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Microsoft.Extensions.Logging;

namespace Everboot.Services;

[Flags]
internal enum IsoLayout
{
    None = 0,
    Iso9660 = 1 << 0,
    Udf = 1 << 1,
}

internal enum WindowsBootEra
{
    /// <summary>Not a recognised Windows install image.</summary>
    None,
    /// <summary>Vista+ (boot.wim + bootmgr / bootmgfw.efi). wimboot-able.</summary>
    Modern,
    /// <summary>XP / 2000 / Server 2003 / NT (i386\\setupldr.bin). PXE-bootable
    /// only via sanboot, and even then unreliable.</summary>
    XpClass,
    /// <summary>Windows 95 / 98 / ME (WIN9x\\SETUP.EXE). Not PXE-bootable
    /// directly - needs a DOS bring-up first.</summary>
    NinexClass,
}

internal sealed record WindowsBootFiles(
    string? Bootmgr,
    string? Bcd,
    string? BootSdi,
    string BootWim,
    string? BcdEfi,
    string? BootmgrEfi)
{
    public bool HasBiosChain => Bootmgr is not null && Bcd is not null && BootSdi is not null;
    public bool HasEfiChain => BcdEfi is not null;
}

internal sealed record IsoMetadata(
    IsoLayout Layout,
    WindowsBootEra WindowsEra,
    WindowsBootFiles? WindowsFiles,
    DistroProfile? Profile,
    string? VolumeLabel,
    string? Squashfs)
{
    public bool IsModernWindows => WindowsEra == WindowsBootEra.Modern && WindowsFiles is not null;
}

/// <summary>
/// Opens an ISO once at scan time, decides whether it is ISO9660/UDF/both,
/// and probes for the file paths required to boot Windows via iPXE+wimboot.
/// Nothing here streams large data - only the directory tables are touched.
/// </summary>
internal sealed class IsoInspector
{
    private static readonly string[] BootWimPaths =
    {
        "sources/boot.wim", "Sources/boot.wim", "SOURCES/BOOT.WIM",
    };

    private static readonly string[] BootmgrPaths = { "bootmgr", "BOOTMGR" };
    private static readonly string[] BcdPaths = { "boot/bcd", "Boot/BCD", "BOOT/BCD" };
    private static readonly string[] BootSdiPaths = { "boot/boot.sdi", "Boot/boot.sdi", "BOOT/BOOT.SDI" };
    private static readonly string[] BcdEfiPaths =
    {
        "efi/microsoft/boot/bcd",
        "EFI/MICROSOFT/BOOT/BCD",
    };
    private static readonly string[] BootmgrEfiPaths =
    {
        "efi/microsoft/boot/bootmgfw.efi",
        "EFI/MICROSOFT/BOOT/BOOTMGFW.EFI",
        "efi/boot/bootx64.efi",
        "EFI/BOOT/BOOTX64.EFI",
    };

    // XP / 2000 / Server 2003 / NT setup loader - lives next to a flat i386 or
    // amd64 tree, no boot.wim. Same boot architecture across all of these.
    private static readonly string[] XpSetupLdrPaths =
    {
        "i386/setupldr.bin", "I386/SETUPLDR.BIN",
        "amd64/setupldr.bin", "AMD64/SETUPLDR.BIN",
    };

    // Win 95 / 98 / 98SE / ME setup. Different SKUs put files under different
    // top-level dirs; presence of any of these SETUP.EXE paths is the marker.
    private static readonly string[] Win9xSetupPaths =
    {
        "win98/setup.exe", "WIN98/SETUP.EXE",
        "win95/setup.exe", "WIN95/SETUP.EXE",
        "win9x/setup.exe", "WIN9X/SETUP.EXE",
        "winme/setup.exe", "WINME/SETUP.EXE",
    };

    private readonly ILogger<IsoInspector> _logger;

    public IsoInspector(ILogger<IsoInspector> logger)
    {
        _logger = logger;
    }

    public IsoMetadata Inspect(string isoPath)
    {
        IsoLayout layout = IsoLayout.None;
        var era = WindowsBootEra.None;
        WindowsBootFiles? winFiles = null;
        DistroProfile? profile = null;
        string? volumeLabel = null;
        string? squashfs = null;

        try
        {
            using var stream = File.Open(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            try
            {
                stream.Position = 0;
                if (CDReader.Detect(stream))
                {
                    layout |= IsoLayout.Iso9660;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ISO9660 detect failed on {Path}", isoPath);
            }

            try
            {
                stream.Position = 0;
                if (UdfReader.Detect(stream))
                {
                    layout |= IsoLayout.Udf;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UDF detect failed on {Path}", isoPath);
            }

            if (layout == IsoLayout.None)
            {
                // Raw disk images (.img, dd dumps, Pi cards, etc.) legitimately
                // don't carry ISO9660/UDF - they will fall through to sanboot,
                // which is the right behaviour for them.
                _logger.LogDebug("{Path} matched neither ISO9660 nor UDF; treating as raw image", isoPath);
                return new IsoMetadata(IsoLayout.None, WindowsBootEra.None, null, null, null, null);
            }

            // Prefer ISO9660+Joliet for Windows boot file probing - boot.wim etc. live
            // on the ISO9660 side of every Windows install ISO since Vista.
            stream.Position = 0;
            DiscFileSystem? fs = null;
            try
            {
                fs = layout.HasFlag(IsoLayout.Iso9660)
                    ? new CDReader(stream, joliet: true)
                    : new UdfReader(stream);

                volumeLabel = TryReadVolumeLabel(fs);
                (era, winFiles) = ProbeWindowsEra(fs);
                profile = DistroProfiles.Match(
                    volumeLabel,
                    path => IsoFileResolver.Resolve(fs, path) is not null);

                if (profile is { SquashfsDir: not null, SquashfsPattern: not null })
                {
                    squashfs = FindLargestMatchingFile(fs, profile.SquashfsDir, profile.SquashfsPattern);
                    if (squashfs is not null)
                    {
                        _logger.LogInformation(
                            "{File}: discovered squashfs at {Sub}",
                            Path.GetFileName(isoPath), squashfs);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "{File}: squashfs probe '{Dir}/{Pattern}' found nothing",
                            Path.GetFileName(isoPath), profile.SquashfsDir, profile.SquashfsPattern);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open filesystem inside {Path}", isoPath);
            }
            finally
            {
                (fs as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not inspect {Path}", isoPath);
            return new IsoMetadata(IsoLayout.None, WindowsBootEra.None, null, null, null, null);
        }

        return new IsoMetadata(layout, era, winFiles, profile, volumeLabel, squashfs);
    }

    /// <summary>
    /// Find the largest file under <paramref name="directory"/> whose name
    /// matches the glob (<c>*.squashfs</c> / <c>*.img</c>). Returned path uses
    /// forward slashes and is relative to the ISO root. Tries a few common
    /// case variants of the directory.
    /// </summary>
    /// <summary>
    /// Well-known filenames we expect to find for each glob, in priority order.
    /// Checked first via the existing case-insensitive <see cref="IsoFileResolver"/>
    /// before falling back to directory enumeration.
    /// </summary>
    private static readonly Dictionary<string, string[]> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["*.squashfs"] = new[]
        {
            "filesystem.squashfs",
            "minimal.squashfs",
            "minimal.standard.squashfs",
            "ubuntu-server-minimal.squashfs",
        },
        ["*.img"] = new[]
        {
            "squashfs.img",
            "rootfs.img",
        },
    };

    private static string? FindLargestMatchingFile(DiscFileSystem fs, string directory, string pattern)
    {
        // 1. Fast path - try well-known filenames first via the resolver we
        //    already know works (this is what the HTTP /iso route uses).
        if (WellKnownNames.TryGetValue(pattern, out var knownNames))
        {
            foreach (var name in knownNames)
            {
                var probe = directory.TrimEnd('/', '\\') + "/" + name;
                var resolved = IsoFileResolver.Resolve(fs, probe);
                if (resolved is not null)
                {
                    return resolved.Replace('\\', '/').TrimStart('/');
                }
            }
        }

        // 2. Fallback - case-insensitive directory walk + glob enumeration
        //    for layouts where the filename isn't on our well-known list.
        var parts = directory.Replace('/', '\\').Trim('\\').Split('\\');
        var current = "\\";
        foreach (var part in parts)
        {
            string[] dirs;
            try
            {
                dirs = fs.GetDirectories(current);
            }
            catch
            {
                return null;
            }
            string? match = null;
            foreach (var d in dirs)
            {
                var leaf = d.TrimEnd('\\', '/').Split('\\', '/')[^1];
                if (string.Equals(leaf, part, StringComparison.OrdinalIgnoreCase))
                {
                    match = d;
                    break;
                }
            }
            if (match is null)
            {
                return null;
            }
            current = match;
        }

        return SearchDirForLargest(fs, current, pattern);
    }

    private static string? SearchDirForLargest(DiscFileSystem fs, string directory, string pattern)
    {
        string[] files;
        try
        {
            files = fs.GetFiles(directory);
        }
        catch
        {
            return null;
        }

        string? bestPath = null;
        long bestSize = -1;
        foreach (var file in files)
        {
            var name = file.Split('\\', '/')[^1];
            if (!MatchesGlob(name, pattern))
            {
                continue;
            }
            long size;
            try
            {
                size = fs.GetFileLength(file);
            }
            catch
            {
                continue;
            }
            if (size > bestSize)
            {
                bestSize = size;
                bestPath = file;
            }
        }
        if (bestPath is null)
        {
            return null;
        }
        return bestPath.Replace('\\', '/').TrimStart('/');
    }

    private static bool MatchesGlob(string name, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadVolumeLabel(DiscFileSystem fs)
    {
        try
        {
            var label = fs.VolumeLabel;
            return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static (WindowsBootEra Era, WindowsBootFiles? Files) ProbeWindowsEra(DiscFileSystem fs)
    {
        // Modern (Vista+) - look for boot.wim and at least one usable chain.
        var bootWim = FindAny(fs, BootWimPaths);
        if (bootWim is not null)
        {
            var bootmgr = FindAny(fs, BootmgrPaths);
            var bcd = FindAny(fs, BcdPaths);
            var bootSdi = FindAny(fs, BootSdiPaths);
            var bcdEfi = FindAny(fs, BcdEfiPaths);
            var bootmgrEfi = FindAny(fs, BootmgrEfiPaths);

            var biosOk = bootmgr is not null && bcd is not null && bootSdi is not null;
            if (biosOk || bcdEfi is not null)
            {
                return (
                    WindowsBootEra.Modern,
                    new WindowsBootFiles(bootmgr, bcd, bootSdi, bootWim, bcdEfi, bootmgrEfi));
            }
            // boot.wim present but no usable chain -> fall through; nothing actionable.
        }

        // XP / 2000 / 2003 / NT4 - setupldr.bin in i386 or amd64.
        if (FindAny(fs, XpSetupLdrPaths) is not null)
        {
            return (WindowsBootEra.XpClass, null);
        }

        // Windows 9x family - WIN9x\\SETUP.EXE.
        if (FindAny(fs, Win9xSetupPaths) is not null)
        {
            return (WindowsBootEra.NinexClass, null);
        }

        return (WindowsBootEra.None, null);
    }

    private static string? FindAny(DiscFileSystem fs, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolved = IsoFileResolver.Resolve(fs, candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        return null;
    }
}

internal static class IsoFileResolver
{
    /// <summary>
    /// DiscUtils filesystems accept both '/' and '\' depending on the reader.
    /// Try the user-supplied form first, then the alternate separator, then a
    /// case-insensitive directory walk as a last resort. Returns the path
    /// shape that actually exists, or null.
    /// </summary>
    public static string? Resolve(DiscFileSystem fs, string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
        {
            return null;
        }

        var normalized = requestPath.Replace('\\', '/').TrimStart('/');
        var backslashed = normalized.Replace('/', '\\');

        if (TryExists(fs, normalized))
        {
            return normalized;
        }
        if (TryExists(fs, backslashed))
        {
            return backslashed;
        }

        return TryCaseInsensitive(fs, normalized);
    }

    private static bool TryExists(DiscFileSystem fs, string p)
    {
        try { return fs.FileExists(p); }
        catch { return false; }
    }

    private static string? TryCaseInsensitive(DiscFileSystem fs, string normalized)
    {
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var currentDir = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            string[] candidates;
            try
            {
                candidates = i == parts.Length - 1
                    ? fs.GetFiles(currentDir.Length == 0 ? "\\" : currentDir)
                    : fs.GetDirectories(currentDir.Length == 0 ? "\\" : currentDir);
            }
            catch
            {
                return null;
            }

            string? match = null;
            foreach (var candidate in candidates)
            {
                var leaf = candidate.Split('\\', '/')[^1];
                if (string.Equals(leaf, parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = candidate;
                    break;
                }
            }
            if (match is null)
            {
                return null;
            }
            currentDir = match;
        }

        return currentDir;
    }
}
