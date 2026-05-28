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
5. The menu is rendered. For Linux/generic ISOs the entry runs `sanboot http://<everboot>/isos/<name>.iso`. For Windows ISOs the entry runs the `wimboot` chain, with `iseq ${platform} efi` branching when the ISO contains both BIOS and UEFI boot files.
6. User picks an entry and boots.

`sanboot` works for most modern hybrid Linux ISOs. For **Windows install ISOs** Everboot detects them automatically (looks for `sources/boot.wim` inside the image) and serves the menu entry as a `wimboot` chain. The required files (`bootmgr`, `boot/bcd`, `boot/boot.sdi`, `sources/boot.wim`) are streamed straight out of the original `.iso` on demand via `/iso/<slug>/<path>` - no extraction to disk, no temporary copies. You only need to drop the `wimboot` loader binary into `data/tftp/wimboot` once; Everboot warns at startup if it is missing while Windows ISOs are present.

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
