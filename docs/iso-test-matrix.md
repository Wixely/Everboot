# Streamed media boot investigation

Tested and reviewed: **2026-10-23**. Branch: `experimental/streaming-boot`.

Nine supplied images were tested on Windows with QEMU 11.1.0, TCG, BIOS,
IDE devices, and Everboot's HTTP-range-to-NBD bridge. Each run used a disposable
QCOW2 disk or overlay. Original media was opened read-only; no host disks were
attached. Guest networking was disabled except for explicitly restricted
diagnostic tests. These results do not establish bare-metal PXE compatibility.

**Six images reached a useful boot milestone:** two desktops, three installers,
and one recovery menu. Five use normal launcher presets after the detection
fixes; Serenity requires the diagnostic configuration below. No complete Windows
or Ubuntu installation, activation, disk recovery, or disk erasure was performed.

| Supplied image | Configuration | Observed result | Remaining work |
| --- | --- | --- | --- |
| Windows 7 Ultimate SP1 x64 | Modern: 2 GiB, qemu64 | Graphical Setup language screen | Complete installation and reboot from the persistent disk |
| Geek XP 41 | Classic: 512 MiB, Pentium III | XP Setup partition selection; sees the disposable 8 GiB IDE disk | Complete installation; this modified image does not establish stock XP compatibility |
| Ubuntu 10.10 Server amd64 | Modern: 2 GiB, qemu64 | Debian-installer language selection after loading the kernel/initrd | Continue through installation; network-dependent steps were not tested |
| SliTaz rolling | Classic: 512 MiB, Pentium III | Graphical desktop | 128 MiB panicked during initramfs expansion; package downloads were not tested |
| Super Grub2 Disk hybrid 2.04s1 | Legacy: 128 MiB, Pentium II | Interactive recovery menu | Test against a deliberately broken disposable OS installation |
| Serenity OS | Raw streamed disk, 2 GiB, direct kernel; see below | Graphical desktop and terminal; root filesystem writes reach the disposable overlay | Package a reusable launch recipe; ordinary ISO launch cannot boot this file |
| iPXE | Legacy, no NIC | iPXE initializes, then reports no network devices | Needs an explicit network configuration and boot target; not a working network boot in this test |
| DBAN 2.3.0 i586 | Legacy, Classic, and Modern with Intel CPU override | Boot prompt and kernel startup; stalls around USB/PCI discovery | Interactive drive selection not verified; no erase attempted |
| Windows 98 SE retail full | Legacy | BIOS cannot boot this CD: no El Torito boot record | Pair with a compatible DOS startup disk and CD driver, then test Setup |

The Windows 98 pairing experiment successfully booted a separate FreeDOS 1.4
startup floppy while keeping the Windows CD attached. It reached the FreeDOS
installer, **not Windows Setup**. A matching Windows 98 startup disk is the
recommended next test; FreeDOS boot alone does not prove Setup compatibility.

The iPXE restricted-network experiment also reached QEMU's fallback NIC option
ROM, which obtained DHCP but had no boot filename. That is **not counted as a
successful boot from the supplied iPXE ISO**. A future test should disable the
NIC ROM to distinguish it from the CD's older iPXE build.

## Streaming evidence

QMP media counters at the captured milestones (cumulative read bytes, not unique
bytes or memory usage):

| Image | Image size in bytes | Media bytes read | Failed reads |
| --- | ---: | ---: | ---: |
| Windows 7 | 3,320,903,680 | 172,945,408 | 0 |
| XP | 789,882,880 | 39,800,832 | 0 |
| Ubuntu Server | 672,688,128 | 11,604,386 | 0 |
| SliTaz | 51,126,272 | 50,790,624 | 0 |
| Super Grub2 | 16,361,472 | 3,348,480 | 0 |

Windows 7 reached Setup without fetching its complete ISO. SliTaz reads nearly
all of its small CD and expands its root filesystem into guest RAM by design.
Streaming removes the mandatory whole-image client buffer; it cannot remove
an operating system's own memory requirements. Kernel/initrd staging still
applies to the direct-kernel Serenity experiment.

Screenshots, QMP logs, and scripts are local, ignored artifacts under
`artifacts/iso-matrix/`. They are not distributed with the project.

## Fixes from these tests

- Prefer UDF when an image has both UDF and ISO9660. This Windows 7 DVD exposes
  only a stub on its ISO9660 side. Detection, HTTP file reads, NFS exports, and
  SMB shares now use the same filesystem selection.
- Recognize classic Ubuntu Server installer media using its label and
  `install/vmlinuz`; select a 64-bit VM automatically. No untested direct PXE
  recipe is advertised.
- Recognize SliTaz using its label and `boot/bzImage`; default to Classic
  instead of the insufficient 128 MiB legacy preset.
- Remove trailing NUL padding from media labels, observed on the XP image.
- Extend `VmProbe` with inspection, scripted waits/screenshots, preset overrides,
  raw-image overrides, and local-only diagnostic QEMU arguments.

Validation: Release build with warnings treated as errors; **474 StreamingProbe
checks passed on Windows and WSL Linux**. The actual Windows 7 WIM header matched
across HTTP range serving, the NFS export reader, and the SMB share reader.
This last check exercises the filesystem backends, not remote NFS/SMB clients.
The real QEMU overlay read/write/reopen test also passed on Windows.

## Serenity reproduction

Despite its `.iso` extension, this file is an ext filesystem image, not an
ISO9660/UDF CD or a disk with an installed bootloader. Its `boot/Prekernel` and
`boot/Kernel` were extracted to local test artifacts. The filesystem itself
remained streamed through NBD, with writes in QCOW2.

Pass these additional QEMU arguments through a JSON array to `VmProbe`:

```json
[
  "-kernel", "C:/test/serenity/Prekernel",
  "-initrd", "C:/test/serenity/Kernel",
  "-append", "hello root=ata0:0:0 serial_debug disable_virtio",
  "-cpu", "max,vendor=GenuineIntel,vmx=off,-x2apic"
]
```

```powershell
dotnet run --project tools/VmProbe -c Release -- testisos/serenity_os.iso --raw --profile modern --qemu-args C:/test/serenity/args.json
```

Using the default AMD vendor exposed a high reserved memory region and this old
kernel panicked in physical-page initialization. The Intel vendor configuration
passed that point. The older `/dev/hda` root argument was rejected; `ata0:0:0`
selected the whole filesystem device and reached the desktop. These findings
apply to this supplied 2022 image, not every Serenity release.

Primary references, reviewed 2026-10-23:

- [Serenity issue #14952](https://github.com/SerenityOS/serenity/issues/14952)
  records the same physical-page initialization failure signature.
- [Serenity boot-device implementation history](https://github.com/SerenityOS/serenity/commits/master/Kernel/Storage/StorageManagement.cpp)
  explains the period's ATA device addressing; the source as of 2022-10-12 was
  inspected to choose the root argument.
- [QEMU direct kernel boot](https://qemu-project.gitlab.io/qemu/system/linuxboot.html)
  documents the kernel/initrd/command-line launch mechanism.

## Recommended next work

1. **Owner: development.** Add declarative launch recipes for media type,
   auxiliary startup floppy or kernel/initrd, CPU compatibility, and explicit
   guest networking. Resolve media by contents rather than extension. Keep the
   current single Start action; do not expose arbitrary QEMU arguments in the UI.
2. **Owner: test maintainer (TBD).** Supply a licensed Windows 98 startup disk and
   finish one XP and one Windows 7 installation/reboot test using persistent VM
   disks. This is the next strongest check of legacy compatibility.
3. **Owner: development.** Investigate DBAN's startup stall and test iPXE against
   an isolated boot server. These are useful compatibility cases, not release
   requirements for the streaming mechanism.
