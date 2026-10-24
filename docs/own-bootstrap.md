# Starting a nonbootable Windows 98 CD

Tested: **2026-10-24**, branch `experimental/streaming-boot`.

The supplied Windows 98 SE retail CD has no El Torito boot record. It now
reaches **graphical Windows 98 Setup** using Everboot-generated startup support,
with its original CD streamed over the normal HTTP/NBD path. No separately
supplied startup floppy, downloaded boot image, FreeDOS, GRUB, or third-party
bootloader is required. Full Windows 98 installation is not yet verified.

## One-time server preparation

On Windows, with the CD already in the server's ISO library:

```powershell
Everboot.exe --prepare-win98 C:/Everboot/data/isos/windows98.iso C:/Everboot/data/tftp
```

Refresh the client library and click **Start virtual machine**. The generated
profile selects the legacy preset and automatically starts Windows Setup.
Its normal installer prompts still apply; this is not an unattended installer.
Preparation is an explicit administrator step in this prototype, not automatic
ISO detection. Existing boot profiles are never overwritten.

The command reads `WIN98/BASE5.CAB` and `WIN98/WIN98_25.CAB`, extracts eight
specific DOS startup/tools files through the Windows `SetupIterateCabinetW` API,
and builds a 1.44 MiB FAT12 startup image in `data/tftp`. It creates a matching
`<image>.vm.json` beside the CD. Original ISO contents are unchanged. This
measured cabinet layout and DOS 7.1 loader are required; other Windows versions,
editions, and languages have not been tested.

Preparation requires Windows. Serving the resulting files and constructing the
guest's FAT16 disk do not require Windows, although this actual Windows 98 boot
has only been tested on a Windows host. No cabinet extractor process or extra
runtime package is required. Temporary extracted files are removed on completion.

## What we own and what comes from the CD

- `tools/boot/dos98.S`: original 16-bit bootstrap, assembled to 512 bytes. It
  reads the first four contiguous sectors of `IO.SYS` and uses the DOS 7.1
  MSLOAD calling convention. It includes no Microsoft boot-sector code.
- `tools/boot/mbr.S`: original 512-byte MBR, which loads the active partition
  at sector 63 using BIOS extended disk reads.
- C# code constructs the FAT12 filesystem, FAT16 installation disk, and startup
  scripts. These are part of Everboot.
- The user's CD supplies `WINBOOT.SYS` (renamed `IO.SYS`), `COMMAND.COM`,
  `HIMEM.SYS`, `OAKCDROM.SYS`, `MSCDEX.EXE`, `FDISK.EXE`, `FORMAT.COM`, and
  `SYS.COM`. These contain Microsoft/Oak code; they are **not** Everboot code and
  are **not bundled or committed**. The generated image contains these files
  from that user's media and should stay with their installation assets.

Internally QEMU sees a generated startup floppy. The user does not provide one.
The startup image is downloaded to disk and attached read-only; the full CD
remains streamed. Existing QEMU/firmware and .NET dependencies are unchanged.

## Installation disk safety and limits

The generated profile uses `InitialDisk: "fat16"`. This creates a roughly 2 GiB
FAT16 partition **only when creating a new private VM disk**. It never reformats
an existing disk or writes a host disk. The raw staging file is converted to
QCOW2 and removed. Allow about 2 GiB of temporary disk space during first launch.
This layout always selects a 2 GiB disk, regardless of the RAM/CPU preset.

This option changes the saved-machine identity, so an older blank test machine
is preserved separately. Profiles without this option retain their previous
identity. Until Setup installs its loader, the generated hard-disk boot sector
asks BIOS to try the next device, allowing a later startup to reach the CD's
generated boot support again.

This is a bounded Windows 98 prototype, not a universal way to boot arbitrary
data CDs. Missing boot metadata can be supplied; missing OS startup software
cannot be invented by a generic boot sector.

## Verification and development

Both embedded binaries are assembled solely from the checked-in original
sources. Normal .NET builds use these binaries and need no assembler. To rebuild
or verify them using GNU binutils in an already configured WSL distribution:

```powershell
./tools/boot/build.ps1 -Verify
```

Omit `-Verify` to replace the embedded binaries after editing the assembly.
No tools are installed by this script. The existing VS Code QEMU client and
integration probe configurations cover this code; stepping 16-bit guest code
would require a separate QEMU/GDB session.

Windows integration checks cover the native cabinet API with synthetic data;
cross-platform checks cover FAT12 allocation, loader rejection, FAT16 geometry,
existing-disk protection, and saved-profile identity compatibility. Actual boot
evidence and disks are ignored local artifacts under `artifacts/own-bootstrap`.

**505 checks passed on Windows; 503 passed on WSL Linux** (the two native cabinet
checks are Windows-only). Both boot binaries reproduced byte-for-byte from their
assembly sources. Release builds passed with warnings treated as errors.
The prepared ISO copy and original had identical SHA-256 hashes, and re-running
preparation refused to overwrite the existing profile.

With QEMU 11.1.0 on Windows, the supplied 652,738,560-byte CD reached graphical
Setup with 128 MiB guest RAM. At that milestone, QMP recorded 1,761,280 bytes
read from the streamed CD, zero source writes, and zero CD/disk read or write
errors. These are cumulative I/O counters, not a measurement of host RAM.
A fresh QEMU process using the same unfinished disk successfully traversed our
MBR and the empty partition's fallback, then restarted Setup from generated
startup support. This is not a completed installation or installed-OS boot.

**Recommended next action — owner: maintainer:** complete a licensed Windows 98
installation and cold reboot, then test another CD edition before making
preparation automatic. Review date: **2026-10-24**.

API reference: [Microsoft's cabinet extraction API](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupiteratecabinetw).
