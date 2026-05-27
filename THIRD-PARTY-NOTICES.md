# Third-Party Notices

Everboot is distributed under the MIT License (see `LICENSE`). It links against
the following third-party components. All are under permissive (non-copyleft)
licenses; full license text for each can be retrieved from the linked source.

## .NET Runtime and `Microsoft.Extensions.*`

- Components: `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`,
  `Microsoft.Extensions.Configuration.Binder`, the .NET 10 base class library
  and runtime.
- Copyright (c) .NET Foundation and Contributors.
- License: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## ZLogger

- Component: `ZLogger` (zero-allocation structured logger).
- Copyright (c) Cysharp, Inc.
- License: MIT — https://github.com/Cysharp/ZLogger/blob/main/LICENSE

## DiscUtils

- Components: `DiscUtils.Streams`, `DiscUtils.Iso9660`, `DiscUtils.Udf`
  (read-only ISO9660 + UDF filesystem access used to stream files out of
  ISO images without extracting them to disk).
- Copyright (c) Quamotion and DiscUtils contributors.
- License: MIT — https://github.com/DiscUtils/DiscUtils/blob/develop/LICENSE.txt

## wimboot (not bundled)

- Component: `wimboot` — the Windows Imaging loader used by iPXE to boot
  modern (Vista+) Windows installation media.
- Everboot does **not** redistribute the `wimboot` binary because it is
  licensed under GPL-2.0. Users who want Windows boot support must drop
  the binary into `data/tftp/wimboot` themselves; Everboot only serves the
  bytes via HTTP. Upstream: https://ipxe.org/wimboot

## memdisk / syslinux (not bundled)

- Component: `memdisk` — the syslinux ramdisk loader used to boot
  floppy-class `.img` files in memory.
- Everboot does **not** redistribute the `memdisk` binary because the
  syslinux project is GPL-2.0 licensed. Users who want floppy boot
  support must drop the binary into `data/tftp/memdisk` themselves;
  Everboot only serves the bytes via HTTP.
  Upstream: https://kernel.org/pub/linux/utils/boot/syslinux/

---

No GPL, LGPL, AGPL, MPL, EPL or other reciprocal/"viral" licensed components
are included. If you add a dependency, verify its license and update this
file in the same change.
