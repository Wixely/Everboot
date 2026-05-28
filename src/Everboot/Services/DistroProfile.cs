using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Everboot.Services;

internal enum DistroFamily
{
    Unknown,
    LinuxLive,
    LinuxInstaller,
    RecoveryTool,
    MemoryTester,
    /// <summary>Recognised but flagged as a known bad / dead-end choice.</summary>
    Unmaintained,
    /// <summary>Recognised but conceptually not a PXE target (e.g. USB multi-boot tools).</summary>
    NotForPxe,
}

/// <summary>
/// Codified kernel + initrd recipe for a given distro. Booted via:
///   <c>kernel http://server/iso/{slug}/{KernelPath} {KernelArgs}</c>
///   <c>initrd http://server/iso/{slug}/{InitrdPath}</c>
///
/// <para>Placeholders <c>{server}</c>, <c>{slug}</c>, <c>{file}</c> are
/// substituted at menu generation time.</para>
///
/// <para>This is deliberately spartan - we only codify direct-boot for distros
/// we are sure about. Everything else falls back to plain sanboot.</para>
/// </summary>
internal sealed record DirectBoot(string KernelPath, string InitrdPath, string KernelArgs);

internal sealed record DistroProfile(
    string Id,
    string DisplayName,
    DistroFamily Family,
    Regex? VolumeLabelPattern,
    string[] MarkerFiles,
    DirectBoot? DirectBoot,
    string? Caveat)
{
    public bool Matches(string? volumeLabel, Func<string, bool> hasFile)
    {
        var labelHits = VolumeLabelPattern is not null
            && !string.IsNullOrEmpty(volumeLabel)
            && VolumeLabelPattern.IsMatch(volumeLabel);

        var fileHits = MarkerFiles.Length > 0 && MarkerFiles.Any(hasFile);

        if (VolumeLabelPattern is not null && MarkerFiles.Length > 0)
        {
            // Many distros share generic markers like live/vmlinuz (Debian
            // Live, Kali, Tails, Clonezilla, GParted, Finnix all use it), so
            // when a profile specifies both a label pattern and markers,
            // require both. Remastered ISOs that have had their label
            // stripped will fall through to sanboot - we lose speedup but
            // avoid mis-identifying.
            return labelHits && fileHits;
        }
        if (VolumeLabelPattern is not null)
        {
            return labelHits;
        }
        return fileHits;
    }
}

/// <summary>
/// Catalogue of recognised images. Order matters: profiles are tried top-down,
/// first match wins, so put more-specific rules first.
/// </summary>
internal static class DistroProfiles
{
    private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    public static readonly DistroProfile[] All =
    {
        // -----------------------------------------------------------------
        // Ubuntu and downstream (Mint, Pop, elementary) - all use casper.
        //
        // Crucial detail: casper running under PXE cannot see iPXE's sanboot
        // virtual disk after the kernel takes over (firmware-only handoff).
        // The working pattern is `netboot=httpfs` + `fetch=<squashfs-url>` so
        // casper downloads the squashfs over plain HTTP via its own initrd
        // wget. We point fetch at our in-ISO streaming route so the squashfs
        // is read on the fly straight out of the original .iso file - no
        // extraction, no per-ISO cache.
        // -----------------------------------------------------------------
        new("ubuntu", "Ubuntu", DistroFamily.LinuxLive,
            new Regex("^ubuntu", Opt),
            new[] { "casper/vmlinuz", "casper/initrd" },
            new DirectBoot(
                "casper/vmlinuz",
                "casper/initrd",
                "boot=casper netboot=httpfs ip=dhcp fetch=http://{server}/iso/{slug}/casper/filesystem.squashfs url=http://{server}/isos/{file} ---"),
            null),

        new("mint", "Linux Mint", DistroFamily.LinuxLive,
            new Regex("^linux *mint", Opt),
            new[] { "casper/vmlinuz", "casper/initrd" },
            new DirectBoot(
                "casper/vmlinuz",
                "casper/initrd",
                "boot=casper netboot=httpfs ip=dhcp fetch=http://{server}/iso/{slug}/casper/filesystem.squashfs url=http://{server}/isos/{file} ---"),
            null),

        new("popos", "Pop!_OS", DistroFamily.LinuxLive,
            new Regex("^pop[_ ]?os|^pop!_os", Opt),
            new[] { "casper/vmlinuz", "casper/initrd" },
            new DirectBoot(
                "casper/vmlinuz",
                "casper/initrd",
                "boot=casper netboot=httpfs ip=dhcp fetch=http://{server}/iso/{slug}/casper/filesystem.squashfs url=http://{server}/isos/{file} ---"),
            null),

        // -----------------------------------------------------------------
        // Debian installer (small netinst). Live Debian uses a versioned
        // /live/vmlinuz-* name we can't reliably codify, so skip direct boot.
        // -----------------------------------------------------------------
        new("debian-netinst", "Debian Installer", DistroFamily.LinuxInstaller,
            new Regex("^debian", Opt),
            new[] { "install.amd/vmlinuz", "install/vmlinuz" },
            new DirectBoot(
                "install.amd/vmlinuz",
                "install.amd/initrd.gz",
                "---"),
            null),

        new("debian-live", "Debian Live", DistroFamily.LinuxLive,
            new Regex("^debian", Opt),
            new[] { "live/filesystem.squashfs" },
            null,
            null),

        // -----------------------------------------------------------------
        // Other major distros - detect only, no direct boot.
        // -----------------------------------------------------------------
        // Fedora Workstation Live uses dracut + dmsquash-live. root=live:URL
        // tells dracut to fetch the squashfs over HTTP. Same trick as casper:
        // bypass iPXE's sanboot disk and let the kernel pull the rootfs.
        new("fedora", "Fedora", DistroFamily.LinuxLive,
            new Regex("^fedora", Opt),
            new[] { "LiveOS/squashfs.img", "isolinux/vmlinuz", "images/pxeboot/vmlinuz" },
            new DirectBoot(
                "isolinux/vmlinuz",
                "isolinux/initrd.img",
                "root=live:http://{server}/iso/{slug}/LiveOS/squashfs.img rd.live.image ip=dhcp"),
            null),

        new("rhel-family", "RHEL-family installer", DistroFamily.LinuxInstaller,
            new Regex("^(rhel|rocky|alma|centos|oracle)", Opt),
            new[] { "images/pxeboot/vmlinuz" },
            null,
            null),

        new("arch", "Arch Linux", DistroFamily.LinuxLive,
            new Regex("^arch", Opt),
            new[] { "arch/boot/x86_64/vmlinuz-linux", "arch/boot/x86_64/initramfs-linux.img" },
            null,
            null),

        new("manjaro", "Manjaro", DistroFamily.LinuxLive,
            new Regex("^manjaro", Opt),
            new[] { "manjaro/boot/vmlinuz-x86_64", "boot/initramfs-x86_64.img" },
            null,
            null),

        new("kali", "Kali Linux", DistroFamily.LinuxLive,
            new Regex("^kali", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            null,
            null),

        new("tails", "Tails", DistroFamily.LinuxLive,
            new Regex("^tails", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            null,
            null),

        new("opensuse", "openSUSE", DistroFamily.LinuxLive,
            new Regex("^opensuse", Opt),
            new[] { "boot/x86_64/loader/linux", "boot/x86_64/loader/initrd" },
            null,
            null),

        new("alpine", "Alpine Linux", DistroFamily.LinuxLive,
            new Regex("^alpine", Opt),
            new[] { "boot/vmlinuz-lts", "boot/vmlinuz-virt" },
            null,
            null),

        new("nixos", "NixOS", DistroFamily.LinuxLive,
            new Regex("^nixos", Opt),
            new[] { "boot/bzImage", "boot/initrd" },
            null,
            null),

        new("proxmox", "Proxmox VE", DistroFamily.LinuxInstaller,
            new Regex("^proxmox|^pve", Opt),
            new[] { "boot/linux26" },
            null,
            null),

        // -----------------------------------------------------------------
        // Recovery and live-utility tools
        // -----------------------------------------------------------------
        new("clonezilla", "Clonezilla Live", DistroFamily.RecoveryTool,
            new Regex("^clonezilla", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            null,
            null),

        new("gparted", "GParted Live", DistroFamily.RecoveryTool,
            new Regex("^gparted", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            null,
            null),

        new("systemrescue", "SystemRescue", DistroFamily.RecoveryTool,
            new Regex("^sysresc|^systemrescue|^rescue", Opt),
            new[] { "sysresccd/boot/x86_64/vmlinuz", "boot/x86_64/vmlinuz" },
            null,
            null),

        new("shredos", "ShredOS", DistroFamily.RecoveryTool,
            new Regex("^shredos", Opt),
            new[] { "shredos/boot/ShredOS.x86_64.efi", "boot/grub/grub.cfg" },
            null,
            null),

        new("rescatux", "Rescatux", DistroFamily.RecoveryTool,
            new Regex("^rescatux", Opt),
            Array.Empty<string>(),
            null,
            null),

        new("grml", "Grml", DistroFamily.RecoveryTool,
            new Regex("^grml", Opt),
            new[] { "boot/grml64-full/vmlinuz", "boot/grml64/vmlinuz" },
            null,
            null),

        new("finnix", "Finnix", DistroFamily.RecoveryTool,
            new Regex("^finnix", Opt),
            new[] { "live/vmlinuz" },
            null,
            null),

        new("memtest", "Memtest86+", DistroFamily.MemoryTester,
            new Regex("^memtest", Opt),
            new[] { "boot/memtest", "memtest86+.bin", "memtest86+.efi" },
            null,
            null),

        // -----------------------------------------------------------------
        // Known-bad: dead projects and tools that aren't a PXE fit.
        // -----------------------------------------------------------------
        new("dban", "DBAN", DistroFamily.Unmaintained,
            new Regex("^dban", Opt),
            new[] { "isolinux/dban.bzi", "dban.bzi" },
            null,
            "unmaintained since 2015 - misses NVMe/modern SSDs; use ShredOS instead"),

        new("ventoy", "Ventoy", DistroFamily.NotForPxe,
            new Regex("^ventoy", Opt),
            new[] { "ventoy/ventoy.json", "ventoy/ventoy.cpio" },
            null,
            "USB multi-ISO loader - Everboot already does this directly"),
    };

    /// <summary>
    /// Match the first profile whose criteria pass.
    /// </summary>
    public static DistroProfile? Match(string? volumeLabel, Func<string, bool> hasFile)
    {
        foreach (var profile in All)
        {
            if (profile.Matches(volumeLabel, hasFile))
            {
                return profile;
            }
        }
        return null;
    }
}
