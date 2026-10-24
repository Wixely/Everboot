# VM boot profiles

Reviewed: **2026-10-24**. Experimental; Windows is the primary tested host.

An administrator can attach a small JSON profile to an image. Clients still
click **Start virtual machine**. Everboot selects the media device, stages and
verifies any small startup files, and starts QEMU. The main ISO/disk remains
streamed; no complete-image download is required.
Update the server and clients together: older clients do not understand boot
profiles and will continue to infer the media type from the filename.

Place the profile beside its image, retaining the complete image filename:

```text
data/
  isos/
    example.iso
    example.iso.vm.json
  tftp/
    startup.img
```

Support files use the existing `data/tftp` folder and `/files/<name>` HTTP
endpoint. No QEMU command line, client filesystem path, URL, or executable is
accepted in the profile. Guest networking stays disabled.

## CD plus startup floppy

For a nonbootable installation CD, copy a compatible, legally obtained startup
floppy image into `data/tftp`, then add:

```json
{
  "Media": "cd",
  "Profile": "legacy",
  "Floppy": "startup.img"
}
```

On the first launch, the startup floppy boots and its CD driver can access the
streamed installation CD. It is attached read-only. The VM receives a separate
writable installation disk. Later launches prefer that hard disk, with CD/floppy
fallback; the firmware boot menu is also available.

The Windows 98 test CD needs a compatible DOS boot environment and CD driver.
The pairing mechanism was tested using FreeDOS 1.4, reaching its startup UI.
That does not establish that FreeDOS can complete Windows 98 Setup. No Microsoft
startup disk or OS files are bundled. A later [Everboot-owned bootstrap](own-bootstrap.md)
now reaches Windows 98 Setup using startup files extracted from the supplied CD,
without requiring a separately supplied startup disk.

## Serenity 2022 filesystem image

The supplied Serenity file is an ext filesystem despite its `.iso` extension.
Extract its `boot/Prekernel` and `boot/Kernel` once on the server, place them in
`data/tftp` with the following names, and create `serenity_os.iso.vm.json`:

```json
{
  "Media": "disk",
  "Profile": "modern",
  "Cpu": "intel-max",
  "Kernel": "serenity-prekernel",
  "Initrd": "serenity-kernel",
  "CommandLine": "hello root=ata0:0:0 serial_debug disable_virtio"
}
```

This profile reached the supplied image's graphical desktop using the normal
launcher code path. It replaces the earlier diagnostic QEMU arguments. The root
filesystem stays streamed with writes in a local QCOW2 overlay. The kernel and
initrd are downloaded to disk before boot and reused after checksum validation.
Other Serenity versions may need different settings.

## Fields and limits

| Field | Values and meaning |
| --- | --- |
| `Media` | Required: `cd`, `disk`, or `floppy`; overrides the filename extension |
| `Profile` | Optional: `legacy` (128 MiB), `classic` (512 MiB), or `modern` (2 GiB); otherwise use image detection |
| `Cpu` | Optional: `intel-max` selects the tested Intel-vendor compatibility CPU; otherwise use the preset's CPU |
| `Kernel` | Optional support filename, up to 128 MiB; requires `disk` media |
| `Initrd` | Optional support filename, up to 256 MiB; requires `Kernel` |
| `CommandLine` | Optional kernel command line, up to 4096 characters; requires `Kernel` |
| `Floppy` | Optional startup filename, up to 4 MiB; requires `cd` media and cannot be combined with `Kernel` |
| `InitialDisk` | Optional `fat16`: initialize only a new private installation disk with a roughly 2 GiB FAT16 partition; requires `cd` media |

Names are single filenames, without directories, traversal, or filesystem links.
Unknown fields and invalid combinations are rejected. Missing files disable
Start for that image and display a repair message; other images remain usable.
Reload the library after changing a profile or support file.

The server publishes each asset's size and SHA-256 checksum. The client downloads
only from that server, refuses redirects, streams to a temporary disk file through
a 64 KiB buffer, verifies size/checksum, and then replaces the cached asset
atomically. This detects changed or corrupt downloads; it is not a signature or
an authenticity guarantee for an untrusted server. Use a trusted server.

Profiles and asset hashes form part of the saved-machine identity alongside the
server URL, image size, and chosen preset. Changing any of those can select a
new virtual disk; the old disk remains available in the VM data directory.
Existing images without a sidecar keep their previous saved-machine identity.
Back up saved disks with guests stopped. Do not change media during an active VM.

## Repeatable installation tests

`VmProbe` supports persistent disposable test disks without using a production
VM library:

```powershell
dotnet run --project tools/VmProbe -c Release -- C:/test/windows.iso --port 38182 --machine-data C:/test/machines --server-data C:/test/server-run1
```

`--server-data` must name a **new** folder. It holds the probe's server-side copy
and is retained after exit. To reopen the saved machine, use the same port,
source image, preset and `--machine-data`, with a new `--server-data` folder.
Default probes still use and remove a temporary server directory. Keep long
installation tests in explicit folders to avoid external temporary-file cleanup.

Additional commands: `key shift+f10` sends a chord; `text testuser` types lowercase
ASCII letters/digits/spaces/hyphens; `stats C:/test/stats.json` saves counters;
`powerdown` requests an ACPI shutdown; `reset` forces a reset. Prefer guest-native
shutdown after installation, then quit and relaunch the probe to verify a cold
boot from the saved disk. `--local-media` is a diagnostic control that attaches
the source directly instead of streaming it, and must not count as a streaming
success. `--recipe file.vm.json --support-dir C:/test/support` copies a profile
and its support folder into the probe server for a real launch test.

Remaining work, owner: maintainer: verify the generated Windows 98 startup support
through installation, extend profiles only with measured compatibility needs,
and test interruption/recovery on a separate physical client/server network.
