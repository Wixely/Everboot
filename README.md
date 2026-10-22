# Everboot

> All-in-one PXE boot server: DHCP proxy, TFTP, HTTP, and per-client boot menu generation in a single binary or container.

Everboot bundles the moving pieces of a typical PXE pipeline into one process so you can stand up netboot from bare metal with a single deploy. Drop ISOs into a folder, point your switch's DHCP at Everboot, and machines on the network get a menu of bootable images.

## What it runs

| Service | Purpose |
| --- | --- |
| **DHCP proxy** | ProxyDHCP per RFC 4578: listens for PXE-marked DHCPDISCOVERs, returns an OFFER carrying `siaddr`, `file`, and option 60 = `PXEClient` so the firmware merges it with the IP allocation from your real DHCP server. Detects arch (option 93) and chainloads iPXE clients (user-class `iPXE`) straight to the HTTP menu. Not a full DHCP authority. |
| **TFTP server** | RFC 1350 read-only server with blksize/tsize options. Serves the small bootstrap binaries PXE firmware fetches first (`undionly.kpxe`, `snponly.efi`, `ipxe.efi`) out of `data/tftp/`. |
| **HTTP file server** | Serves `/boot.ipxe` (the dynamic menu) and `/isos/<name>.iso` (the boot payloads) to iPXE. |
| **iSCSI target** | Read-only target on TCP/3260. Exposes each `.iso` as a SCSI CD-ROM and each `.img` as a direct-access disk, with a stable IQN per image (`{IqnBase}:iso-<slug>`). Optional sanboot path — `Iscsi:UseForSanboot=true` switches menu sanboot entries from `http://...iso` to `iscsi:...`, which boots more reliably than HTTP-as-SAN. |
| **SMB2 target** | Read-only SMB 2.0.2 server on TCP/445. Each ISO is exposed as a share named by its slug, backed by DiscUtils streaming the ISO contents on demand — never extracted to disk. Lets WinPE (after wimboot) do `net use Z: \\everboot\<slug>` and find `Z:\sources\install.wim` so Windows Setup completes. No NTLM auth (accept-anything) in this MVP — enough for the WinPE → install-source workflow. |
| **Boot config generator** | Renders the iPXE menu from the contents of `data/isos/` (both `*.iso` and `*.img`). |
| **NFSv3 (experimental)** | Read-only ISO file trees over TCP, with embedded MOUNTv3 and portmapper v2. Compatible Linux live environments mount these trees after kernel handoff instead of downloading the whole payload into RAM. |

## Experimental streaming boot

Branch: `experimental/streaming-boot`. Reviewed: **2026-10-22**.

This branch embeds a read-only **NFSv3 server in the same C# process**. Each
ISO becomes an export at `/<image-slug>`, backed directly by DiscUtils. There is
no ISO extraction, separate NFS daemon, initramfs modification, or new native
dependency. The client loads its kernel and initrd, then mounts the ISO's full
file tree over NFS and reads the live filesystem on demand. This preserves
multiple SquashFS layers and installer files instead of guessing a single
root filesystem to download.

Drop images into `data/isos`, start Everboot, network-boot a client, and select
an image once. Compatible Casper and Debian Live layouts prefer NFS automatically.
**Advanced boot options** retains the download-to-RAM and SAN alternatives.
Failures before kernel handoff return to that menu without an automatic retry
loop; failures after handoff require rebooting the client.

The web library shows streaming candidates, download-to-RAM methods, missing
wimboot, NFS listener state, and NFS read counters. Counters are since startup,
exclude HTTP kernel/initrd transfers, and update when the page is reloaded.

### Compatibility and limits

- Ubuntu/Mint/Pop Casper layouts (`casper/vmlinuz` and `casper/initrd` or
  `initrd.lz`) and Debian Live-family layouts (`live/vmlinuz`, `live/initrd.img`)
  receive experimental NFS recipes when their live filesystem is present.
  Detection checks file layout, **not initramfs driver contents or successful
  boot**. Renamed kernel paths, customized network initialization, distro-specific
  boot arguments, and multi-layer `layerfs-path` selection may need new profiles.
- The OS needs working NIC and NFS client support in its initramfs. The recipes
  use IPv4, NFSv3 over TCP, explicit mount/NFS ports, read-only mounts, and no
  lock daemon. They do not use `fetch=`, `url=`, or `toram` on the streaming path.
- Kernel, initrd, application working memory, reclaimable page cache, and the
  writable live-session overlay still consume RAM. The ISO itself does not need
  a reserved client RAM region. The server and network must remain available.
- Windows retains wimboot and the existing SMB source. Automatic WinPE-to-Setup
  orchestration and a diskless Windows driver are **not implemented** here.
  Unknown/raw images retain compatibility paths; SAN boot alone does not prove
  that the OS can keep using the network disk after firmware handoff.
- This server exposes a read-only ISO9660/Joliet or UDF view. It does not preserve
  Rock Ridge POSIX symlinks/permissions or provide a general-purpose writable NFS
  filesystem. Handles expire when the server restarts or the image changes.
- Use a trusted boot network: exports are readable without authentication or
  encryption, like the existing HTTP image catalog. Do not replace an ISO during
  an active boot. No local target disk is written by the streaming service.

### Legacy operating systems

Legacy support is experimental; **no DOS, Windows 9x, or XP boot has yet been
verified**. Selecting an image still chooses a method automatically. Windows 9x
is no longer blocked from attempting boot, and NT/2000/XP media retains the SAN
path. These Windows paths require BIOS/CSM; UEFI clients receive an explanation
before downloading the image. Nonbootable installation CDs still need separate
boot media; detecting setup files does not make a CD bootable.

HTTP and iSCSI ISO boot uses CD drive `0xe0`, keeping the local hard-disk number
available. Raw disk images use `0x80`. The RAM fallback now distinguishes ISO,
floppy, and hard-disk images and rejects UEFI before downloading. Small DOS
floppies can use this fallback without reserving RAM for a full installation CD.
It still loads the entire selected floppy into RAM.

| OS/media | Current path | What remains |
| --- | --- | --- |
| DOS boot floppy | BIOS MEMDISK floppy | Real boot and conventional-memory checks |
| Bootable DOS/9x CD | BIOS HTTP/iSCSI SAN attempt | Compatible BIOS/El Torito DOS CD driver and access after boot |
| NT/2000/XP/2003 | BIOS SAN attempt | OS-specific NIC/storage integration; unmodified setup can lose the source after handoff |
| Older Linux | Detected kernel/initrd recipe or SAN attempt | Distribution-specific initramfs network/NFS support; NFSv2-only clients are unsupported |

Firmware disk hooks are not a universal OS storage driver. DOS/9x can use BIOS
disk access, while protected-mode native drivers may bypass it. Everboot's SMB2
server is not a source for stock SMB1-only DOS/9x/XP clients. See the primary
[MEMDISK compatibility notes](https://wiki.syslinux.org/wiki/index.php?title=MEMDISK)
and [iPXE SAN boot documentation](https://ipxe.org/cmd/sanboot).

A generic path across those driver boundaries would need persistent virtual
hardware (for example a local VM with a streamed IDE CD/disk), or individual
native OS drivers. A VM appliance is an architecture option, **not implemented
or selected by this branch**. Owner: maintainer. Next validation: boot freely
redistributable DOS media in a BIOS VM, confirm subsequent CD file reads, then
test user-supplied licensed Windows media through protected-mode handoff.
Reviewed: **2026-10-22**.

### Configuration and deployment

NFS defaults to TCP **111** (portmapper v2), **2049** (NFSv3), and **20048**
(MOUNTv3). Allow these on the boot network alongside the existing PXE/HTTP ports.
No NFS UDP ports or host OS NFS service are required. If a host NFS/rpcbind service
already occupies a port, choose different ports; generated recipes include them.
`Development` binds NFS to loopback on 32111/32049/32048, so it is not a LAN boot
configuration. Use Production for real clients and set `Everboot:Dhcp:ServerAddress`
to a reachable IPv4 address when HTTP uses a hostname or reverse proxy.

```json
"Nfs": {
  "Enabled": true,
  "BindAddress": "0.0.0.0",
  "Port": 2049,
  "MountPort": 20048,
  "PortmapperPort": 111,
  "MaxConnections": 32
},
"Menu": { "OneClickBoot": true }
```

These sections belong inside `Everboot`. Environment equivalents include
`Everboot__Nfs__Enabled=false` to restore download/SAN defaults and
`Everboot__Menu__OneClickBoot=false` to restore the per-image method submenu.
Build this branch locally for Docker (`docker build -t everboot:streaming .`);
the published `latest` image does not contain this experiment. Host networking
remains the supported deployment model; Docker packaging has not been run in
this experiment. Existing normal .NET single-file publishing is retained;
NativeAOT compatibility has not been established for the existing dependencies.

### Verification and next acceptance test

Run the dependency-free integration probe:

```powershell
dotnet build Everboot.sln -c Release
dotnet run --project tools/StreamingProbe -c Release
```

The probe creates temporary **synthetic, non-bootable** ISOs and checks generated
recipes, one-selection routing, missing-file handling, NFS reads/EOF, read-only
errors, directory pagination, stale handles, malformed requests, and fragmented
TCP RPC. VS Code includes a `Streaming integration checks` debug configuration.
Run with `-- --serve` to keep its fixture available for ten minutes. On a Linux
host with NFS client support, an independent kernel mount can be checked with:

```sh
mkdir -p /tmp/everboot-nfs-check
sudo mount -t nfs -o ro,nolock,vers=3,proto=tcp,mountproto=tcp,port=32049,mountport=32048 127.0.0.1:/ubuntu /tmp/everboot-nfs-check
sha256sum /tmp/everboot-nfs-check/casper/filesystem.squashfs
ls /tmp/everboot-nfs-check/casper
sudo umount /tmp/everboot-nfs-check
rmdir /tmp/everboot-nfs-check
```

Compare the hash with the probe's printed expected hash. A Linux system without
`mount.nfs` can use `mount -i` with the additional option `addr=127.0.0.1` if its
kernel supports NFS's text mount interface. The `--serve` probe listens on loopback.

Verified on 2026-10-22: Release build; self-contained Windows executable startup;
444 protocol/integration checks on Windows and
Linux under WSL; an independent Linux-kernel NFSv3 mount reading the complete
12 MiB fixture with an exact SHA-256 match, listing 203 entries, and rejecting a
write; Chromium desktop/mobile library layout. This is **not** a completed OS boot
or a measured low-RAM acceptance result.

**Next action (owner: maintainer):** boot a real Debian Live ISO, then an Ubuntu
ISO, in a disposable VM with less RAM than the ISO size but enough for the distro's
working set. Record ISO checksum, architecture, BIOS/UEFI mode, RAM, time to desktop,
NFS bytes read, and continued reads after login. Test network reconnection, concurrent
clients, and Secure Boot separately. Review before promotion to `main`; no universal
ISO compatibility is claimed. Protocol basis: [NFSv3](https://www.rfc-editor.org/rfc/rfc1813),
[Casper](https://manpages.ubuntu.com/manpages/jammy/man7/casper.7.html), and
[Debian live-boot](https://manpages.debian.org/trixie/live-boot-doc/live-boot.7.en.html).

## The data folder

Everboot serves everything out of a single data directory (default `./data` next to the binary, `/data` in Docker, `Everboot:DataDirectory` to override):

```
data/
├── isos/    drop *.iso files here - one per menu entry
└── tftp/    iPXE/syslinux bootstrap binaries
```

Add or remove an ISO and the menu updates within ~500 ms - no restart needed. The file name (minus `.iso`) becomes the menu label; rename it to whatever you want users to see.

## How a client boots

1. Client firmware PXE-boots and broadcasts DHCPDISCOVER (vendor class `PXEClient`, arch in option 93).
2. Your network DHCP allocates an IP as normal. Everboot's proxy sees the same DISCOVER, recognises the PXE marker, and answers with `siaddr` = Everboot and `file` = the iPXE loader matching the client's arch (`undionly.kpxe` for BIOS, `snponly.efi` for UEFI x64, `snponly-arm64.efi` for ARM64, etc.). The client merges both replies.
3. Client TFTPs the iPXE binary from Everboot and runs it.
4. iPXE does its own DHCPDISCOVER, this time with user-class `iPXE`. Everboot recognises that and answers with `file` = `http://<everboot>:8080/boot.ipxe`, so iPXE chainloads the menu directly over HTTP.
5. The menu is rendered. Compatible Linux live layouts prefer direct kernel/initrd boot with NFS. Unknown images use `sanboot http://<everboot>/isos/<name>.iso` as a compatibility attempt. For Windows ISOs the entry runs the `wimboot` chain, with `iseq ${platform} efi` branching when the ISO contains both BIOS and UEFI boot files.
6. User picks an entry and boots.

`sanboot` supplies a firmware-visible disk; continued access after OS handoff depends on the image. The experimental NFS recipes above provide OS-native network access for compatible Linux layouts. For **Windows install ISOs** Everboot detects them automatically (looks for `sources/boot.wim` inside the image) and serves the menu entry as a `wimboot` chain. The required files (`bootmgr`, `boot/bcd`, `boot/boot.sdi`, `sources/boot.wim`) are streamed straight out of the original `.iso` on demand via `/iso/<slug>/<path>` - no extraction to disk, no temporary copies. You only need to drop the `wimboot` loader binary into `data/tftp/wimboot` once; Everboot warns at startup if it is missing while Windows ISOs are present.

## Quick start

```bash
dotnet run --project src/Everboot
```

Then drop an ISO into `data/isos/` and visit `http://localhost:8080/` for a human-readable index, or `http://localhost:8080/boot.ipxe` for the menu iPXE will see. Files inside an ISO are reachable at `http://localhost:8080/iso/<slug>/<path-inside-iso>` (used for the Windows boot chain).

Logs land in the terminal and on disk (`./logs/everboot-YYYY-MM-DD.NNN.log`, daily roll + 10 MB cap). Override the log directory with `EVERBOOT_LOG_DIR`.

> **Privileged ports.** TFTP (`69/udp`) and the DHCP proxy (`67/udp`) need
> admin/root to bind. In Docker that means running with `--cap-add NET_BIND_SERVICE`
> (or just as root, which the bundled image does). The Development profile
> moves TFTP to `6969` and HTTP to `localhost:8080` so `dotnet run` works
> without elevation.
>
> On Windows, binding HTTP on `+:8080` in production needs admin or a one-off URL ACL:
> `netsh http add urlacl url=http://+:8080/ user=Everyone`.

## Build a standalone binary

```bash
dotnet publish src/Everboot -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Replace `linux-x64` with `win-x64` for a Windows binary.

## Run in Docker

For a one-off `docker run`:

```bash
docker build -t everboot:dev .
docker run --rm --network host \
  -v /srv/everboot/data:/data \
  -v /var/log/everboot:/var/log/everboot \
  everboot:dev
```

For a real deployment (Portainer, Compose, etc.) use the sample stack at [examples/docker-compose.yml](examples/docker-compose.yml) — it pulls `ghcr.io/wixely/everboot:latest`, uses host networking, bind-mounts `data/` + `logs/`, and has every env-var override pre-documented inline.

`--network host` is the simplest option — PXE/DHCP need raw access to the broadcast domain. Mount your ISO library at `/data/isos/`.

## Configuration

`appsettings.json` (or `Everboot__*` env vars):

```json
{
  "Everboot": {
    "DataDirectory": "./data",
    "Http": { "Port": 8080, "BindAddress": "+" },
    "Tftp": { "Port": 69, "BindAddress": "0.0.0.0", "MaxBlockSize": 1468 },
    "Iscsi": {
      "Enabled": true,
      "Port": 3260,
      "IqnBase": "iqn.2026-05.local.everboot",
      "UseForSanboot": false
    },
    "Dhcp": {
      "Enabled": true,
      "Port": 67,
      "ServerAddress": null,
      "BootFiles": {
        "Bios": "undionly.kpxe",
        "Uefi32": "snponly-i386.efi",
        "Uefi64": "snponly.efi",
        "UefiArm64": "snponly-arm64.efi"
      }
    },
    "Menu": { "Title": "Everboot - select an image", "TimeoutSeconds": 60 }
  }
}
```

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
