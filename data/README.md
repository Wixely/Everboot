# Everboot data directory

Set `Everboot:DataDirectory` (or `Everboot__DataDirectory`) to the data folder.
It contains two subdirectories:

```text
data/
  isos/   ISO and raw IMG files
  tftp/   iPXE bootstrap binaries, wimboot, optional memdisk
```

## Add an image

Copy a complete `.iso` or `.img` into `isos/`. The catalog updates automatically;
the web library also has a Refresh catalog button. The filename supplies the menu
label. For large copies, copy with a different extension first, then rename when
complete. Do not replace or remove an image while clients are using it.

On the experimental streaming branch, selecting an image starts its recommended
method immediately. **Advanced boot options** retains manual methods. Set
`Everboot:Menu:OneClickBoot=false` for the older method-submenu behavior.

## How the image is served

- **Casper and Debian Live layouts:** when the expected kernel, initrd, and live
  filesystem exist, prefer an experimental NFS recipe. Each image is exported as
  `/<slug>`. Files are read directly from the ISO, without extraction or a separate
  host NFS server. The client needs NFS and network drivers in its initramfs.
  A streaming candidate is a layout match, not a successful boot certification.
- **Modern Windows:** load `boot.wim` into RAM through wimboot. The ISO's installation
  files are available through the existing read-only SMB share. WinPE setup source
  selection remains manual; this experiment does not add automatic Windows Setup.
- **Other recognized Linux layouts:** use validated kernel/initrd paths where a
  profile exists. HTTP `fetch=` and ISO download recipes still consume client RAM.
- **Unknown images and legacy Windows:** SAN boot is a compatibility attempt.
  Firmware-visible virtual disks may disappear when the OS takes over; this does
  not establish streaming support for an arbitrary ISO or raw disk image.
- **Small floppy images:** use memdisk and load the image into RAM. Windows 9x
  install media and Ventoy images retain their explanatory unsupported messages.

The whole image is also available at `/isos/<filename>` over HTTP. Individual
files are available at `/iso/<slug>/<path>`. Both routes support byte ranges.
The web library and `/diagnose` show detection and applicable boot recipes.

See the [streaming guide](../README.md#experimental-streaming-boot) for ports,
configuration, protocol limits, verification evidence, and the outstanding
low-RAM OS boot acceptance test. The existing `latest` container image does not
contain this experimental branch; build the branch locally to test it.

## Bootstrap files

Place the loaders for your client architectures in `tftp/`:

- `undionly.kpxe` for legacy BIOS; `snponly.efi` or `ipxe.efi` for x64 UEFI;
  other architectures need their corresponding configured iPXE binary.
- `wimboot` for Windows PE boot. Get it from [iPXE](https://ipxe.org/wimboot).
  Missing wimboot is reported in the library and startup log.
- `memdisk` for floppy images, from [Syslinux](https://kernel.org/pub/linux/utils/boot/syslinux/).

These are served by TFTP and `/files/<name>` over HTTP. They are not supplied by
the streaming implementation. Keep their license/source information when distributing
them. Kernel/initrd downloads, OS working memory, and writable overlays still use
RAM even when the live filesystem is streamed.
