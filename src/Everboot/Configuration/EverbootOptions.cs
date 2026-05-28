using System.ComponentModel.DataAnnotations;

namespace Everboot.Configuration;

internal sealed class EverbootOptions
{
    public const string SectionName = "Everboot";

    [Required]
    public string DataDirectory { get; set; } = "./data";

    /// <summary>
    /// Periodic catalog rescan interval in seconds, in addition to the
    /// FileSystemWatcher-driven rescans. Catches changes when the watcher
    /// misses events (bind-mounts, network FS, Docker volumes). Set to 0
    /// to disable periodic rescans entirely. Minimum effective interval
    /// is 5 seconds to avoid hammering disk.
    /// </summary>
    [Range(0, 86400)]
    public int CatalogRescanIntervalSeconds { get; set; } = 60;

    public HttpOptions Http { get; set; } = new();

    public TftpOptions Tftp { get; set; } = new();

    public DhcpOptions Dhcp { get; set; } = new();

    public IscsiOptions Iscsi { get; set; } = new();

    public SmbOptions Smb { get; set; } = new();

    public BootMenuOptions Menu { get; set; } = new();
}

internal sealed class HttpOptions
{
    [Range(1, 65535)]
    public int Port { get; set; } = 8080;

    /// <summary>
    /// HttpListener bind host. "+" listens on every interface (needs admin/URLACL on Windows),
    /// "localhost" is the safe dev default, or set a specific IP.
    /// </summary>
    public string BindAddress { get; set; } = "+";
}

internal sealed class TftpOptions
{
    /// <summary>
    /// UDP port to listen on. Default 69 is the PXE-standard port and needs
    /// root / admin to bind. Use a higher port for unprivileged dev testing.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 69;

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Largest blksize a client may negotiate. 1468 leaves headroom under a
    /// 1500 MTU with IP + UDP + TFTP headers.
    /// </summary>
    [Range(512, 65464)]
    public int MaxBlockSize { get; set; } = 1468;

    [Range(50, 60000)]
    public int BlockTimeoutMs { get; set; } = 1500;

    [Range(1, 20)]
    public int MaxRetries { get; set; } = 5;

    [Range(1, 1024)]
    public int MaxConcurrentTransfers { get; set; } = 64;
}

internal sealed class DhcpOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 65535)]
    public int Port { get; set; } = 67;

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Our advertised next-server IP (goes into <c>siaddr</c> + option 54 +
    /// the iPXE script URL). Null = auto-detect from local NICs.
    /// </summary>
    public string? ServerAddress { get; set; }

    /// <summary>
    /// Standard ProxyDHCP behaviour is to broadcast OFFERs back. For loopback
    /// smoke testing (where broadcast on 127.0.0.0/8 is awkward), unicast the
    /// reply to the source UDP endpoint instead. Leave false in production.
    /// </summary>
    public bool UnicastReplyToSource { get; set; } = false;

    public DhcpBootFiles BootFiles { get; set; } = new();

    /// <summary>
    /// URL handed to iPXE clients (detected via user-class "iPXE"). The literal
    /// <c>{server}</c> token is replaced with the resolved server IP.
    /// </summary>
    public string IpxeScriptUrl { get; set; } = "http://{server}/boot.ipxe";
}

/// <summary>
/// Boot file name (relative to <c>data/tftp/</c>) handed to PXE firmware,
/// keyed by client architecture (DHCP option 93).
/// </summary>
internal sealed class DhcpBootFiles
{
    public string Bios { get; set; } = "undionly.kpxe";
    public string Uefi32 { get; set; } = "snponly-i386.efi";
    public string Uefi64 { get; set; } = "snponly.efi";
    public string UefiArm64 { get; set; } = "snponly-arm64.efi";
}

internal sealed class IscsiOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 65535)]
    public int Port { get; set; } = 3260;

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// IQN root for generated target names. Targets become
    /// <c>{IqnBase}:iso-{slug}</c>. Default uses the RFC 3720 reverse-domain
    /// convention with a placeholder year-month.
    /// </summary>
    public string IqnBase { get; set; } = "iqn.2026-05.local.everboot";

    /// <summary>
    /// Negotiated MaxRecvDataSegmentLength for the target side. iPXE defaults
    /// to 8 KB; 256 KB is the protocol max and works for almost everything.
    /// </summary>
    [Range(512, 16777215)]
    public int MaxReceiveDataSegment { get; set; } = 256 * 1024;

    /// <summary>
    /// When true, sanboot entries in the iPXE menu use <c>iscsi:</c> URLs
    /// pointing at this service instead of <c>http://...iso</c>. Off by
    /// default until you have verified iSCSI works end-to-end on your fleet.
    /// </summary>
    public bool UseForSanboot { get; set; } = false;
}

internal sealed class SmbOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Listen port. Real WinPE clients connect to 445 unconditionally; dev
    /// usually uses a high port to avoid conflicting with the OS SMB service.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 445;

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// NetBIOS name reported in TREE_CONNECT responses. Some clients show
    /// this in error messages.
    /// </summary>
    public string ServerName { get; set; } = "EVERBOOT";

    [Range(4096, 16 * 1024 * 1024)]
    public int MaxReadSize { get; set; } = 1024 * 1024;
}

internal sealed class BootMenuOptions
{
    public string Title { get; set; } = "Everboot - select an image";

    [Range(0, 3600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Each ISO opens a per-image submenu listing every applicable boot
    /// method (auto / direct / wimboot / sanboot http / sanboot iscsi /
    /// memdisk / shell). This is the auto-pick timeout for that submenu.
    /// Set to 0 to disable auto-pick (user must select manually).
    /// </summary>
    [Range(0, 3600)]
    public int MethodMenuTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Console resolution like <c>"1024x768"</c> or <c>"800x600"</c>. When
    /// set, iPXE emits <c>console --x W --y H</c> at the top of the script
    /// to switch the framebuffer to that mode. Gives the menu more rows /
    /// columns than the default 80x25 text mode. Stock iPXE supports this
    /// on UEFI (GOP) and BIOS (VESA). Leave null to keep firmware default.
    /// </summary>
    public string? ConsoleResolution { get; set; }

    /// <summary>
    /// Filename of a PCX-format image dropped into <c>data/tftp/</c> to use
    /// as the menu background (text renders on top). The script emits
    /// <c>console --picture http://server/files/&lt;name&gt;</c>. Requires a
    /// custom iPXE build with <c>CONSOLE_PCX</c> compiled in — not in the
    /// stock binaries. Leave null to skip.
    /// </summary>
    public string? BackgroundImage { get; set; }
}
