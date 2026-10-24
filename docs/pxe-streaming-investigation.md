# PXE streaming investigation

Review date: **2026-10-24**. Branch: `experimental/streaming-boot`.

Everboot is a PXE server. QEMU is a network-booted test machine, not the product
or the mechanism that presents a remote ISO as local hardware. Earlier QEMU
virtual-CD, NBD overlay, and Windows 98 startup-image tests do **not** establish
PXE compatibility. Those experiments remain separate from this investigation.

## The required handoff

PXE/iPXE starts the boot process; the operating system then needs its own way
to keep reading the remote filesystem or disk. Supplying BIOS disk hooks or
an El Torito replacement does not automatically provide that OS-level support.
The workable design is a small bootstrap in RAM plus an on-demand network
source for the larger installation or live-system payload.

| Target | Streaming design | Current Everboot position |
| --- | --- | --- |
| Debian Live / compatible Casper Linux | HTTP kernel/initrd, then guest-native NFS mounting the ISO's file tree and reading SquashFS on demand | NFS and recipes exist; actual PXE test below |
| Linux with suitable HTTP filesystem support | Kernel/initrd plus `httpfs`/FUSE reading the image by HTTP ranges | Possible alternative; requires that support in the initramfs, not yet tested here |
| Modern Windows installation | Load WinPE, initialize its NIC, then run Setup against the ISO-backed SMB share | WIM/file serving and SMB2 exist; automatic network initialization/source connection and complete PXE installation remain unverified |
| Linux or Windows with suitable iSCSI boot support | Firmware starts from iSCSI; OS continues using its native initiator and boot configuration | Read-only ISO target exists; compatible OS handoff must be tested per image |
| DOS / Windows 9x | Small DOS environment plus a network filesystem client or a DOS CD driver that can read the network-backed BIOS device | Research needed; the previous hardware-ATAPI CD driver cannot access a PXE client's nonexistent physical CD |
| XP / 2000 / NT | OS-specific early network/storage support, or stage installation files onto a local target disk | No generic unmodified installer handoff established; stock clients cannot use Everboot's SMB2-only server |
| Arbitrary unknown ISO | Attempt firmware boot only if its OS can continue accessing the source | Cannot promise generic streaming support |

The ISO can remain read-only and unchanged on the server. NFS and SMB expose
files directly from inside it without creating an extracted installation tree.
Some OSes may nevertheless need generated startup configuration, drivers, or
a supplementary initramfs; that should be a separate artifact, not an in-place
rewrite of the ISO. A local-disk staging option avoids whole-image RAM use but
is not a diskless live boot.

There is always a minimum RAM requirement for the kernel, initrd/WinPE, working
memory, cache, and live-session writes. Streaming removes the requirement to
reserve RAM for the entire ISO; it does not make an OS independent of RAM size.

## Actual network-boot test

`tools/PxeProbe` launches QEMU with a NIC and **no attached CD or hard disk**, no
`-kernel`, and no NBD device. NIC iPXE firmware receives Everboot's HTTP boot URL
from QEMU's user-network DHCP. It loads Everboot's generated menu, kernel/initrd,
and then connects to Everboot's NFS service from inside the guest.

This is an isolated test of network boot and storage handoff. QEMU's DHCP stands
in for the LAN DHCP server. It does not validate Everboot's ProxyDHCP broadcast
discovery or a TFTP bootstrap download. No LAN DHCP configuration is changed.

The first Windows-hosted attempt could not bind the wildcard HTTP listener
without an appropriate HTTP.sys permission. No URL ACL was changed. Testing
continued with the same .NET server code and QEMU in the existing WSL Linux
environment. Windows-host PXE and physical-client tests remain outstanding.

Test medium: official Debian Live 13.7.0 amd64 standard, downloaded from Debian's
current-live image directory and checked against its published SHA-256 list:

```text
040a44f35186321eb6cdaab52a8a2d06224fe6b77f6fbb9f1861ad89c14e17ec
```

- ISO: 2,020,130,816 bytes.
- `live/filesystem.squashfs`: 1,284,431,872 bytes.
- Guest RAM: 1,024 MiB (1,073,741,824 bytes), smaller than both.
- No guest hard disk or swap disk is attached.
- ISO and SquashFS stay on the server; kernel/initrd are served from inside ISO.

The initial menu test found a real detection problem: Debian's current label is
`d-live 13.7.0 st`, while the profile only accepted labels starting with `debian`.
The profile now accepts the `d-live` label while still requiring the live
filesystem marker. Regression checks cover both a valid marker and its absence.

The first kernel boot then exposed an initramfs compatibility bug: klibc
`nfsmount` rejects `proto=`, `mountproto=`, and `mountport=`. Generated recipes
now use its supported `tcp` option and discover the MOUNT service through
portmapper on TCP 111. Custom NFS/MOUNT ports still work through that discovery;
a nonstandard portmapper port is only suitable for explicit protocol tests,
not this stock initramfs boot path.

Boot result: **passed for this Debian Live image on a BIOS QEMU PXE client**.
The guest reached its automatic user login shell without manual boot repair.
`findmnt /run/live/medium` confirmed `10.0.2.2:/live`, NFSv3 over TCP,
read-only. `free -m` reported 965 MiB usable RAM, 233 MiB used, 731 MiB
available, and zero swap at that point. Server NFS counters after login were
1,767 reads / 76,922,880 bytes; HTTP kernel/initrd transfers are separate.

After login, `dd if=/run/live/medium/live/filesystem.squashfs of=/dev/null bs=1M skip=900 count=32`
successfully read 33,554,432 bytes. NFS counters increased from 77,053,952 to
110,788,608 bytes (1,769 to 2,284 reads), confirming continued remote reads
rather than a fully cached image. `post-read.png` records the successful command.

Evidence is retained locally under `artifacts/pxe-test/run5`: `boot.png`,
`login.png`, `mounts.png`, `qemu-arguments.json`, and `nfs-samples.jsonl`.
These are test artifacts, not bundled product assets. The test uses BIOS and
an emulated e1000 NIC; UEFI, other NICs, other live images, and physical clients
are not established by this result.

Validation: release probe build with warnings as errors passed; the streaming
regression suite passed 507 checks on Windows and 505 on Linux (platform-specific
cabinet checks account for the difference). These checks complement the actual
network-boot evidence; synthetic protocol tests alone do not prove OS support.

## Reproduce

Build the probe, then run it on a host with .NET 10 and QEMU already installed:

```text
dotnet build tools/PxeProbe -c Release
dotnet tools/PxeProbe/bin/Release/net10.0/PxeProbe.dll /path/to/debian-live.iso /path/to/new-results --ram 1024
```

On Windows, pass `--qemu-dir` if QEMU is not on PATH and use an account with
permission for the probe's HTTP listener. In VS Code, select the PXE streaming
probe debug configuration; it asks for the ISO and a new artifact directory.
The artifact directory retains a server-side ISO copy, QEMU arguments, images,
and counter samples. It must not already exist. Commands include `shot menu.png`,
`key ret`, `text <ASCII command>`, `stats`, `powerdown`, and `quit`.

Default HTTP/NFS/MOUNT ports are 38184/38185/38186; `--port` moves this group.
Portmapper remains on TCP 111 for the stock initramfs client. On Linux the test
process needs permission to bind this privileged port (the WSL test ran as root);
the probe now waits for both services before launching QEMU. No persistent
permission or network configuration was changed. The probe serves HTTP on all interfaces; use a controlled test
host. NFS and QMP bind to loopback, which QEMU's user network exposes to its guest
as `10.0.2.2`. This topology is for tests, not a LAN deployment configuration.

## Next work

1. **Owner: development.** Extend the working Linux path to additional live images
   and UEFI clients, using the same below-image-size RAM acceptance check.
2. **Owner: development.** Extend emulated PXE acceptance coverage to
   ProxyDHCP/TFTP, restart/interruption, and multiple clients. Physical PXE
   hardware is unavailable; it is not a prerequisite for this work.
3. **Owner: development.** Automate WinPE network initialization and connection
   to the ISO-backed installation share, then test a full Windows PXE install.
4. **Owner: development.** Prototype DOS network-media access independently of
   physical ATAPI devices; assess XP/2000 separately before claiming support.

## Primary references

- [Debian live-boot options](https://manpages.debian.org/trixie/live-boot-doc/live-boot.7.en.html): NFS, `httpfs`, `fetch`, and iSCSI paths. `fetch` copies into RAM; `httpfs` mounts in place.
- [Debian Live manual](https://live-team.pages.debian.net/live-manual/html/live-manual.en.html): network-boot images can require a different initramfs configuration.
- [iPXE Windows PE deployment](https://ipxe.org/howto/winpe): WinPE network boot followed by installation from a file server.
- [Microsoft WinPE networking](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-network-drivers-initializing-and-adding-drivers?view=windows-10): driver initialization and network shares.
- [iPXE SAN boot](https://ipxe.org/cmd/sanboot): firmware attachment and iBFT description.
- [iPXE sanbootconf](https://ipxe.org/sanbootconf): XP/2003 iSCSI can require additional OS configuration/driver support; not a dependency added by this investigation.
- [klibc nfsmount source](https://sources.debian.org/src/klibc/2.0.14-1/usr/kinit/nfsmount/): accepted mount options and portmapper discovery.
