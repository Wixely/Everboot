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
/// One codified kernel + initrd recipe for a given distro. Booted via:
///   <c>kernel http://server/iso/{slug}/{KernelPath} {KernelArgs}</c>
///   <c>initrd http://server/iso/{slug}/{InitrdPath}</c>
///
/// <para>Placeholders in <c>KernelArgs</c> substituted at menu generation:
///   <c>{server}</c>   HTTP authority (host:port)
///   <c>{slug}</c>     entry slug (used in URL paths)
///   <c>{file}</c>     URL-encoded ISO filename
///   <c>{squashfs}</c> path inside the ISO of the discovered squashfs/img
///                    (only available when <see cref="DistroProfile.SquashfsDir"/>
///                    is set and IsoInspector found a match)</para>
///
/// <para>A profile can declare multiple recipes - they appear as separate
/// items in the per-ISO method submenu so the user can try each.</para>
/// </summary>
internal sealed record DirectBootRecipe(
    string Id,
    string DisplayName,
    string KernelPath,
    string InitrdPath,
    string KernelArgs);

internal sealed record DistroProfile(
    string Id,
    string DisplayName,
    DistroFamily Family,
    Regex? VolumeLabelPattern,
    string[] MarkerFiles,
    DirectBootRecipe[] DirectBoots,
    string? SquashfsDir,
    string? SquashfsPattern,
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

    private static readonly DirectBootRecipe[] CasperRecipes =
    {
        // Modern casper (16.04+): netboot=httpfs + fetch=<squashfs-url>
        new(
            "casper-httpfs",
            "casper (modern - netboot=httpfs + fetch=)",
            "casper/vmlinuz",
            "casper/initrd",
            "boot=casper netboot=httpfs ip=dhcp fetch=http://{server}/iso/{slug}/{squashfs} url=http://{server}/isos/{file} ---"),
        // Legacy casper (14.04 era): netboot=url, casper downloads & loop-mounts the whole ISO
        new(
            "casper-url",
            "casper (legacy - netboot=url, downloads whole ISO)",
            "casper/vmlinuz",
            "casper/initrd",
            "boot=casper netboot=url url=http://{server}/isos/{file} ip=dhcp ---"),
    };

    private static readonly DirectBootRecipe[] LiveBootRecipes =
    {
        // Debian live-boot with fetch= (squashfs over HTTP)
        new(
            "live-fetch",
            "live-boot (fetch= squashfs over HTTP)",
            "live/vmlinuz",
            "live/initrd.img",
            "boot=live components fetch=http://{server}/iso/{slug}/{squashfs} ip=dhcp ---"),
    };

    private static readonly DirectBootRecipe[] DebianNetinstRecipes =
    {
        new(
            "debian-installer",
            "Debian installer (interactive)",
            "install.amd/vmlinuz",
            "install.amd/initrd.gz",
            "---"),
    };

    private static readonly DirectBootRecipe[] FedoraRecipes =
    {
        new(
            "dracut-http",
            "dracut + dmsquash-live (HTTP)",
            "isolinux/vmlinuz",
            "isolinux/initrd.img",
            "root=live:http://{server}/iso/{slug}/{squashfs} rd.live.image ip=dhcp"),
    };

    private static readonly DirectBootRecipe[] None = Array.Empty<DirectBootRecipe>();

    public static readonly DistroProfile[] All =
    {
        // -----------------------------------------------------------------
        // Ubuntu and downstream (Mint, Pop, elementary) - all use casper.
        //
        // casper under PXE cannot see iPXE's sanboot virtual disk after the
        // kernel takes over (firmware-only handoff). The working pattern is
        // for casper to fetch the squashfs over HTTP via its own wget. Two
        // recipes are codified: the modern netboot=httpfs+fetch= and the
        // legacy netboot=url that fetches the whole ISO. {squashfs} expands
        // to the actual squashfs path discovered inside the ISO at scan time.
        // -----------------------------------------------------------------
        new("ubuntu", "Ubuntu", DistroFamily.LinuxLive,
            new Regex("^ubuntu", Opt),
            new[] { "casper/vmlinuz", "casper/initrd" },
            CasperRecipes, "casper", "*.squashfs", null),

        new("mint", "Linux Mint", DistroFamily.LinuxLive,
            new Regex("^linux *mint", Opt),
            new[] { "casper/vmlinuz", "casper/initrd" },
            CasperRecipes, "casper", "*.squashfs", null),

        new("popos", "Pop!_OS", DistroFamily.LinuxLive,
            new Regex("^pop[_ ]?os|^pop!_os", Opt),
            new[] { "casper/vmlinuz", "casper/initrd" },
            CasperRecipes, "casper", "*.squashfs", null),

        // -----------------------------------------------------------------
        // Debian installer (small netinst).
        // -----------------------------------------------------------------
        new("debian-netinst", "Debian Installer", DistroFamily.LinuxInstaller,
            new Regex("^debian", Opt),
            new[] { "install.amd/vmlinuz", "install/vmlinuz" },
            DebianNetinstRecipes, null, null, null),

        new("debian-live", "Debian Live", DistroFamily.LinuxLive,
            new Regex("^debian", Opt),
            new[] { "live/filesystem.squashfs" },
            LiveBootRecipes, "live", "*.squashfs", null),

        // -----------------------------------------------------------------
        // Other major distros - detect only, no direct boot codified.
        // -----------------------------------------------------------------
        new("fedora", "Fedora", DistroFamily.LinuxLive,
            new Regex("^fedora", Opt),
            new[] { "LiveOS/squashfs.img", "isolinux/vmlinuz", "images/pxeboot/vmlinuz" },
            FedoraRecipes, "LiveOS", "*.img", null),

        new("rhel-family", "RHEL-family installer", DistroFamily.LinuxInstaller,
            new Regex("^(rhel|rocky|alma|centos|oracle)", Opt),
            new[] { "images/pxeboot/vmlinuz" },
            None, null, null, null),

        new("arch", "Arch Linux", DistroFamily.LinuxLive,
            new Regex("^arch", Opt),
            new[] { "arch/boot/x86_64/vmlinuz-linux", "arch/boot/x86_64/initramfs-linux.img" },
            None, null, null, null),

        new("manjaro", "Manjaro", DistroFamily.LinuxLive,
            new Regex("^manjaro", Opt),
            new[] { "manjaro/boot/vmlinuz-x86_64", "boot/initramfs-x86_64.img" },
            None, null, null, null),

        new("kali", "Kali Linux", DistroFamily.LinuxLive,
            new Regex("^kali", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            LiveBootRecipes, "live", "*.squashfs", null),

        new("tails", "Tails", DistroFamily.LinuxLive,
            new Regex("^tails", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            LiveBootRecipes, "live", "*.squashfs", null),

        new("opensuse", "openSUSE", DistroFamily.LinuxLive,
            new Regex("^opensuse", Opt),
            new[] { "boot/x86_64/loader/linux", "boot/x86_64/loader/initrd" },
            None, null, null, null),

        new("alpine", "Alpine Linux", DistroFamily.LinuxLive,
            new Regex("^alpine", Opt),
            new[] { "boot/vmlinuz-lts", "boot/vmlinuz-virt" },
            None, null, null, null),

        new("nixos", "NixOS", DistroFamily.LinuxLive,
            new Regex("^nixos", Opt),
            new[] { "boot/bzImage", "boot/initrd" },
            None, null, null, null),

        new("proxmox", "Proxmox VE", DistroFamily.LinuxInstaller,
            new Regex("^proxmox|^pve", Opt),
            new[] { "boot/linux26" },
            None, null, null, null),

        // -----------------------------------------------------------------
        // Recovery and live-utility tools
        // -----------------------------------------------------------------
        new("clonezilla", "Clonezilla Live", DistroFamily.RecoveryTool,
            new Regex("^clonezilla", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            LiveBootRecipes, "live", "*.squashfs", null),

        new("gparted", "GParted Live", DistroFamily.RecoveryTool,
            new Regex("^gparted", Opt),
            new[] { "live/vmlinuz", "live/initrd.img" },
            LiveBootRecipes, "live", "*.squashfs", null),

        new("systemrescue", "SystemRescue", DistroFamily.RecoveryTool,
            new Regex("^sysresc|^systemrescue|^rescue", Opt),
            new[] { "sysresccd/boot/x86_64/vmlinuz", "boot/x86_64/vmlinuz" },
            None, null, null, null),

        new("shredos", "ShredOS", DistroFamily.RecoveryTool,
            new Regex("^shredos", Opt),
            new[] { "shredos/boot/ShredOS.x86_64.efi", "boot/grub/grub.cfg" },
            None, null, null, null),

        new("rescatux", "Rescatux", DistroFamily.RecoveryTool,
            new Regex("^rescatux", Opt),
            Array.Empty<string>(),
            None, null, null, null),

        new("grml", "Grml", DistroFamily.RecoveryTool,
            new Regex("^grml", Opt),
            new[] { "boot/grml64-full/vmlinuz", "boot/grml64/vmlinuz" },
            None, null, null, null),

        new("finnix", "Finnix", DistroFamily.RecoveryTool,
            new Regex("^finnix", Opt),
            new[] { "live/vmlinuz" },
            LiveBootRecipes, "live", "*.squashfs", null),

        new("memtest", "Memtest86+", DistroFamily.MemoryTester,
            new Regex("^memtest", Opt),
            new[] { "boot/memtest", "memtest86+.bin", "memtest86+.efi" },
            None, null, null, null),

        // -----------------------------------------------------------------
        // Known-bad: dead projects and tools that aren't a PXE fit.
        // -----------------------------------------------------------------
        new("dban", "DBAN", DistroFamily.Unmaintained,
            new Regex("^dban", Opt),
            new[] { "isolinux/dban.bzi", "dban.bzi" },
            None, null, null,
            "unmaintained since 2015 - misses NVMe/modern SSDs; use ShredOS instead"),

        new("ventoy", "Ventoy", DistroFamily.NotForPxe,
            new Regex("^ventoy", Opt),
            new[] { "ventoy/ventoy.json", "ventoy/ventoy.cpio" },
            None, null, null,
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
