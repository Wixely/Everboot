# Everboot data directory

Everboot loads everything it serves from this folder. The path is configurable
(`Everboot:DataDirectory` in `appsettings.json` or `Everboot__DataDirectory`
env var), but by default Everboot looks here.

## Layout

```
data/
├── isos/   *.iso files are auto-discovered and added to the boot menu
└── tftp/   bootstrap binaries served by TFTP + the HTTP /files/ route
            (e.g. ipxe.efi, undionly.kpxe, snponly.efi, wimboot)
```

## isos/

Drop `*.iso` or `*.img` files into `data/isos/` - despite the directory name,
both extensions are picked up. Each file becomes a menu entry at `/boot.ipxe`.
Everboot watches the directory and reloads the menu when files are added,
removed, or renamed - no restart required.

When a new image appears, Everboot opens it (read-only) and looks at the
filesystem inside to decide how to boot it:

- **Linux / generic ISOs and IMGs.** Booted as a virtual disk via
  `sanboot http://<server>/isos/<name>`. iPXE pulls bytes on demand.
  Works for hybrid Linux ISOs, USB images, Ventoy, and most raw disk
  images that are bootable on the client architecture.
- **Modern Windows install ISOs** (Vista+: anything containing
  `sources/boot.wim`). The ISO is *never extracted to disk*. Everboot
  streams the specific files iPXE needs (`bootmgr`, `boot/bcd`,
  `boot/boot.sdi`, `sources/boot.wim`) out of the original file on the fly
  via `/iso/<slug>/<path>`, and the menu entry uses `wimboot` to chain
  into Windows PE. If the ISO also carries the EFI chain
  (`efi/microsoft/boot/bcd` + `bootmgfw.efi`) the menu branches on
  `${platform}` so BIOS and UEFI clients each get the right loader.
- **Windows XP / 2000 / Server 2003** (detected via `i386/setupldr.bin`).
  Tagged in the menu as *"Windows XP-era - PXE may fail"* and served
  via plain `sanboot`. XP-era boot loaders are twitchy over HTTP-backed
  virtual CDs - it works on some firmware combinations, hangs on
  others. Microsoft's blessed PXE-XP path was RIS, which Everboot
  doesn't implement.
- **Windows 95 / 98 / ME** (detected via `WIN9x/setup.exe`). Tagged as
  *"not PXE-bootable"*; the menu entry prints a brief explanation and
  returns to the menu rather than attempting a boot. Win9x install
  needs a DOS environment - the standard workaround is to PXE-boot a
  FreeDOS floppy via memdisk and run `setup.exe` from a network share.
- **Floppy images** (`.img` files ≤ 4 MB with no ISO9660/UDF). Booted
  via the syslinux `memdisk` loader: `kernel memdisk` + the image as
  initrd. Drop `memdisk` into `data/tftp/`; Everboot warns at startup
  if a floppy is present without it.
- **Known Linux distros** (Ubuntu/Mint/Pop, Debian, Fedora, Arch,
  Manjaro, Kali, Tails, openSUSE, Alpine, NixOS, Rocky/Alma, Proxmox)
  are identified by ISO9660 volume label + marker files. For distros
  where the kernel+initrd layout is rock-stable (Ubuntu casper,
  Debian netinst), the menu emits a **direct kernel boot** entry
  (much faster than sanbooting the whole ISO) *plus* a secondary
  "sanboot fallback" item that bypasses the codified profile - useful
  if a distro restructures its ISO and our paths go stale.
- **Recovery / utility tools** (Clonezilla, GParted Live, SystemRescue,
  ShredOS, Grml, Finnix, Memtest86+, Acronis) are detected and tagged
  in the menu but booted via plain sanboot. No direct-kernel profile;
  add one if you have a distro you boot often.
- **Known-bad / not-for-PXE images** are detected and warned:
  - **DBAN** — unmaintained since 2015, misses NVMe and most modern
    SSDs. Tag suggests using ShredOS instead. Entry still attempts
    sanboot in case you really want it.
  - **Ventoy** — a USB multi-ISO loader, no point chainloading from
    PXE since Everboot already does the same thing. Entry prints an
    explanatory message and returns to the menu.

The file name (without `.iso`) is shown as the menu label. Rename the file
to whatever you want users to see, e.g. `Ubuntu 24.04 Server.iso`.

## tftp/

Bootstrap binaries that the PXE firmware (or iPXE itself) needs to fetch
*before* it can render the menu. The same directory is exposed over HTTP
under `/files/<name>` so iPXE can grab them either way.

Drop in whichever loaders you need:

- `ipxe.efi`, `snponly.efi`, `undionly.kpxe` - the iPXE chainload binaries
  pointed to by your DHCP server. Build or download from <https://ipxe.org/>.
- `wimboot` - **required if you want modern Windows ISOs to boot.** Not
  shipped because it is GPL-2.0 licensed. Grab it from
  <https://ipxe.org/wimboot> and drop it at `data/tftp/wimboot`. Everboot
  logs a warning at startup if a Windows ISO is present but `wimboot` is
  not.
- `memdisk` - **required to boot floppy-class `.img` files.** From the
  syslinux project (<https://kernel.org/pub/linux/utils/boot/syslinux/>).
  Drop it at `data/tftp/memdisk`. Same warning fires if a floppy image
  is present without it.

## What is *not* in here

Everboot's logs, configuration, and binaries live elsewhere (`logs/` next
to the binary or `EVERBOOT_LOG_DIR`, `appsettings.json` next to the binary).
This folder is purely the boot payload catalog.
