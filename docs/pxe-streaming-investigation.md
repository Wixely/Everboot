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
| Modern Windows installation | Load WinPE, initialize its NIC, then run Setup against the ISO-backed SMB share | Windows 7 SP1 x64 completed installation to desktop in BIOS QEMU with 1 GiB RAM; other versions and UEFI remain unverified |
| Linux or Windows with suitable iSCSI boot support | Firmware starts from iSCSI; OS continues using its native initiator and boot configuration | Read-only ISO target exists; compatible OS handoff must be tested per image |
| DOS / Windows 9x | Small DOS startup plus a synthesized FAT16 source streamed through iPXE BIOS disk reads | Windows 98 SE reached graphical Setup and product-key screen over PXE with 128 MiB RAM; complete installation remains unverified |
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

By default, `tools/PxeProbe` launches QEMU with a NIC and **no attached CD or hard disk**, no
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

### Windows PE result

In the initial diskless test, the Windows 7 SP1 x64 ISO supplied in `testisos` (3,320,903,680 bytes)
network-boots into WinPE and automatically starts Windows Setup from its
ISO-backed SMB share. The BIOS QEMU client has 1,024 MiB RAM, an e1000 NIC,
and no attached installation CD or hard disk. No guest-side command was needed
in the successful boot. The first Setup screen was reached after 5,496,710
bytes of SMB reads, separate from the HTTP WinPE download.
After selecting Next and Install now, Setup reached its license screen and
the counter increased to 20,894,716 bytes. No target disk was attached and no
installation was performed in that run. The subsequent full installation is
documented below. Final release build passed with warnings as errors;
regression checks passed 533 on Windows and 529 on Linux (Windows adds cabinet
and native SSPI compatibility checks).

The generated wimboot chain supplies `winpeshl.ini` and `everboot.cmd` as
small extra files. They appear in WinPE's RAM filesystem, run `wpeinit`, map
the selected share to `Z:`, and launch `Z:\setup.exe`. Neither the ISO nor
its on-disk `boot.wim` is rewritten. SMB-disabled/nonstandard-port configurations
retain the ordinary WinPE boot without automatic source connection. A failure
opens a troubleshooting shell; exiting it retries the connection.

Real-client testing required fixes beyond the earlier SMB simulator coverage:
SMB1-format negotiation upgrades to SMB2, raw NTLM and SPNEGO guest handshakes,
Linux-independent ISO filename handling, and correctly sized directory-entry
layouts. The server still provides **public guest access**, not authenticated
identity: passwords are not validated or retained. Signing-required or
guest-blocking clients are not supported by this path; no client policy was
weakened. No SMB1 file operations were added.

The test uses the existing WSL environment for the .NET server and QEMU. It
does not establish Windows-host SMB/HTTP listener compatibility, UEFI Windows
boot, or support for later Windows releases. Windows
Setup remains interactive; Everboot does not automatically select or erase a
target disk. Physical PXE hardware is unavailable, so emulated acceptance
tests remain the development path.

Local evidence: `artifacts/pxe-test/winpe9/setup.png`, `next.png`, `license.png`,
`qemu-arguments.json`, and `nfs-samples.jsonl` (now also includes SMB byte counts).
During iteration a server-side symbolic link reused the same ISO; the standard
probe reproduction copies it instead. Some diagnostic runs therefore display
the link length in the catalog; counter samples report the original ISO length.

The test-only wimboot loader is upstream **v2.9.0**, downloaded from
`https://github.com/ipxe/wimboot/releases/download/v2.9.0/wimboot`, SHA-256
`5f067ccdc4d084d5bf77b6c853bd0f8402dfc2b4cd1b103d358993ae97fae8e3`.
It remains a separately supplied GPL loader, not bundled source or a committed
binary. No additional third-party source/library was added for the startup or
SMB compatibility code.

Pass `--wimboot /path/to/wimboot` to PxeProbe to enable its loopback SMB service
on TCP 445 and copy the loader into its temporary catalog. Linux needs permission
to bind that port. `--smb-debug` enables command/status tracing, not authentication
payload logging. Use a Windows installation ISO as the first argument.

For an installation test, add `--disk-gib 32`. The probe creates a new sparse
`target.qcow2` inside the new artifact directory and attaches it as an IDE hard
disk. No existing disk path is accepted. QEMU boots from PXE once, then from
the target disk on subsequent resets. The source ISO is never attached to the
guest; Windows Setup must read its installation payload over SMB. Disk sizes
from 16 to 256 GiB are accepted; omit the option for the original diskless test.

### Linux / shared probe

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

### Complete Windows 7 installation

On **2026-10-24**, the same Windows 7 Ultimate SP1 x64 image completed a fresh
PXE-started installation to its desktop. This extends the earlier diskless
Setup-screen test; the original ISO remained unchanged on the server.

| Property | Observed result |
| --- | --- |
| Guest | QEMU 8.2, TCG, BIOS, e1000, 1,024 MiB RAM |
| Source ISO | 3,320,903,680 bytes; never attached as a virtual CD or disk |
| Installation target | New 32 GiB QCOW2 IDE disk, initially entirely unallocated |
| Bootstrap | NIC iPXE, Everboot menu, HTTP WinPE, generated startup, native SMB |
| Setup | Copy, expansion, features, updates, disk restarts, account setup, desktop all completed |
| SMB payload reads | 3,334,602,010 bytes; cumulative reads include rereads and exclude HTTP WinPE transfers |
| Image consistency | `qemu-img check` after clean guest shutdown: no errors |
| Offline cold boot | New QEMU process, saved target disk only, `-nic none`, no ISO, Everboot stopped: returned to desktop |

No SMB implementation changes were required beyond the previously committed
WinPE compatibility fixes. One image selection started WinPE and network Setup;
the ordinary Windows installer choices were still interactive. This is not an
unattended installer or a diskless Windows runtime. A disposable local account
was used, with no product key, activation, or update-service testing.
Release build passed with warnings as errors, along with 533 Windows and 529
Linux regression checks. Invalid target sizes were rejected before artifact
creation. These checks do not establish Windows-host listener compatibility.

Reproduce on a prepared host with QEMU, .NET 10, the separately supplied wimboot,
and permissions for the probe's listeners:

```text
dotnet tools/PxeProbe/bin/Release/net10.0/PxeProbe.dll /path/to/windows7.iso /path/to/new-results --ram 1024 --wimboot /path/to/wimboot --disk-gib 32
```

Local evidence is retained under `artifacts/pxe-test/winpe-install1/`, including
the initial PXE menu, blank-disk selection, file expansion, post-install desktop,
QEMU arguments, and `nfs-samples.jsonl` (which also records SMB byte counts).
ISOs, target disks, logs, and screenshots are not committed.

## Next work

1. **Owner: development.** Extend the working Linux path to additional live images
   and UEFI clients, using the same below-image-size RAM acceptance check.
2. **Owner: development.** Extend emulated PXE acceptance coverage to
   ProxyDHCP/TFTP, restart/interruption, and multiple clients. Physical PXE
   hardware is unavailable; it is not a prerequisite for this work.
3. **Owner: development. Recommended next action.** Validate a later Windows PE
   image and UEFI separately using the fresh-disk PXE installation workflow.
4. **Owner: development.** Continue the [Windows 98 PXE prototype](windows98-pxe.md)
   through installation and first reboot with a valid media key; assess local
   source staging and XP/2000 separately before claiming support.

## Primary references

- [Debian live-boot options](https://manpages.debian.org/trixie/live-boot-doc/live-boot.7.en.html): NFS, `httpfs`, `fetch`, and iSCSI paths. `fetch` copies into RAM; `httpfs` mounts in place.
- [Debian Live manual](https://live-team.pages.debian.net/live-manual/html/live-manual.en.html): network-boot images can require a different initramfs configuration.
- [iPXE Windows PE deployment](https://ipxe.org/howto/winpe): WinPE network boot followed by installation from a file server.
- [Microsoft WinPE networking](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-network-drivers-initializing-and-adding-drivers?view=windows-10): driver initialization and network shares.
- [iPXE SAN boot](https://ipxe.org/cmd/sanboot): firmware attachment and iBFT description.
- [iPXE sanbootconf](https://ipxe.org/sanbootconf): XP/2003 iSCSI can require additional OS configuration/driver support; not a dependency added by this investigation.
- [klibc nfsmount source](https://sources.debian.org/src/klibc/2.0.14-1/usr/kinit/nfsmount/): accepted mount options and portmapper discovery.
- [iPXE wimboot injected files](https://ipxe.org/wimboot#injected_files): startup files can be supplied separately from the original WIM.
- [Microsoft SMB2 multi-protocol negotiation](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/00a5a917-e5f9-4d31-a0d5-a5ee7209c61f): SMB1-formatted initial negotiation can select SMB2.
- [Microsoft NTLM challenge format](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-nlmp/801a4681-8809-4be9-ab0d-61dcfe762786): challenge fields and target information.
- [Microsoft directory query formats](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/10906442-294c-46d3-8515-c277efe1f752): the requested information class determines the directory-entry layout.
