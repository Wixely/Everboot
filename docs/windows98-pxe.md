# Windows 98 PXE investigation

Reviewed **2026-10-24**. Branch: `experimental/streaming-boot`.

The supplied Windows 98 SE retail ISO now reaches the graphical Setup wizard
and product-key screen through actual BIOS PXE boot. This supersedes the earlier
virtual-CD-only result for that milestone. **Installation and installed-OS boot
are not verified.** A valid product key for the supplied media is needed to
continue the installation test.

## Verified path

| Stage | Result |
| --- | --- |
| Machine | QEMU 8.2, TCG, BIOS, e1000, 128 MiB RAM |
| Source | Original 652,738,560-byte ISO; no attached CD, NBD device, or direct QEMU kernel boot |
| Startup | NIC iPXE receives Everboot's HTTP menu; Everboot-owned FAT12 bootstrap loads DOS files taken from the user's CD |
| Network source | iPXE HTTP SAN exposes a synthesized FAT16 view of `WIN98` through BIOS disk reads |
| Diskless diagnostic | DOS listed `SETUP.EXE` and copied `BASE5.CAB` to `NUL`; 1,948,160 source bytes served |
| Target-disk diagnostic | Fresh 2 GiB FAT16 IDE target at C:, network source at D:, startup at A: |
| Setup | `D:\WIN98\SETUP.EXE /IS` reached graphical welcome, license agreement, and product-key screen |
| Source reads at product-key screen | 7,410,688 bytes; excludes separate startup-image reads |

The diskless diagnostic used SAN drive `0x80`. The current target-disk recipe
uses `0x81`, preserving the local first hard disk, and boots the generated
startup image at `0x00`. It assumes a single local FAT target disk. Machines with
additional disks, different drive letters, UEFI, or other NICs are unverified.
QEMU's DHCP supplied the HTTP boot URL; Everboot ProxyDHCP/TFTP discovery remains
a separate test. The .NET server and QEMU ran in the existing WSL environment.

## How it avoids a whole ISO in RAM

`DosSourceDisk` synthesizes an MBR, FAT16 tables, and a short-name directory in
roughly 336 KiB of metadata. File extents map directly to files inside the
original ISO. HTTP range requests read just the requested payload bytes, using
bounded buffers. No 2 GiB source-image file or extracted setup tree is created.
Metadata caching is bounded to 16 source layouts and invalidated on source
length or modification-time changes.

The virtual source is read-only and contains the top-level files in `WIN98`;
it is not a general ISO-to-FAT converter. Unsupported short names, duplicate
names, missing setup files, and capacity overflow fail preparation explicitly.

Preparation extracts a small set of DOS support components from the user's CD,
as the previous bootstrap did. Those Microsoft/Oak files are not bundled with
Everboot. The bootstrap and FAT projection are project-owned code; no new
third-party DOS network client, SMB1 implementation, or library was added.
iPXE remains the existing separately supplied PXE loader. The original ISO is
unchanged, but **small derived startup assets are required**.

## Reproduce

Prepare on Windows, using the original ISO and a new support directory:

```powershell
dotnet run --project src/Everboot -c Release -- --prepare-win98-pxe 'testisos/Windows 98 SE (101 - Retail Full).iso' artifacts/win98-support
```

This writes `<ISO filename>.dos.img` to the support directory and refuses to
overwrite it. For an Everboot server, place that asset in its `tftp` directory
and the original ISO in its `isos` directory, retaining the ISO filename.
The menu then selects the experimental BIOS DOS source path for that image.

Run the emulated PXE test on a prepared host:

```text
dotnet build tools/PxeProbe -c Release -warnaserror
dotnet tools/PxeProbe/bin/Release/net10.0/PxeProbe.dll /path/to/windows98.iso /path/to/new-results --ram 128 --share-iso-directory --dos-target --dos-startup /path/to/windows98.iso.dos.img
```

`--share-iso-directory` links the artifact catalog to the source directory;
it does not copy the ISO. This exposes that directory's other images to the
test catalog too. Creating directory symlinks must be permitted by the host;
the tested WSL environment supports it. There is no silent copy fallback.

`--dos-target` creates a new 2 GiB FAT16 target only inside the new artifact
directory. It never accepts or formats an existing disk. On the tested Windows
filesystem through WSL this consumed about **2 GiB of host space**, so do not
assume sparse allocation. The probe checks available space for the source copy
(if requested), target capacity, and a 4 GiB reserve before creating artifacts.
Concurrent activity can still change available space after that check.

Select Windows 98 in the PXE menu. The current bootstrap performs a source-read
diagnostic and stops at DOS; type the Setup command displayed there to proceed.
This is still a diagnostic prototype, not the final one-selection installation
experience. `/IS` skips ScanDisk in this controlled test. No user disk is
automatically formatted and no product key is embedded or supplied.

Local evidence: `artifacts/pxe-test/win98-pxe1/` and `win98-pxe2/`, including
screenshots, QEMU arguments, and source counters. Generated support files and
test media remain ignored artifacts. The final source-directory normalization
was also checked by preparing support from the real CD and by synthetic tests.

## Validation and next work

Release build passed with warnings as errors. **548 Windows / 544 Linux** checks
passed, including FAT-chain reconstruction against original ISO bytes, zero
padding, duplicate FAT consistency, bounds, HTTP ranges/HEAD, preparation gating,
and BIOS-only menu routing. Space preflight rejected an oversized target on
Windows and WSL before creating its artifact directory.

**Recommended next action — owner: development, with a licensed media key
provided locally by the user:** complete installation and verify the first
reboot. BIOS network-disk hooks may disappear on restart or be bypassed by native
drivers. Evaluate staging `WIN98` onto the local target before launching Setup
if needed; that would use target disk space, not whole-ISO RAM. Do not advertise
diskless Windows 98 or a completed installation based on the Setup-screen result.

After that, automate the verified startup sequence and test another Windows 98
edition before treating this as a generic legacy-Windows solution.

Protocol basis: [iPXE SAN boot](https://ipxe.org/cmd/sanboot) and
[iPXE BIOS INT 13 implementation documentation](https://dox.ipxe.org/int13_8c.html).
