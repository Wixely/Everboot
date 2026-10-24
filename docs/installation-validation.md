# Installation and boot-profile validation

Test date: **2026-10-24**. Branch: `experimental/streaming-boot`.

This follows the [initial ISO boot investigation](iso-test-matrix.md). Tests use
Windows-hosted QEMU 11.1.0, software emulation, BIOS, IDE CD/disk devices, and no
guest NIC. Installation targets are disposable, persistent QCOW2 disks; original
images are read-only. No host disks or shares are attached.

## Installation results

| Image | Machine | Installation | Cold relaunch |
| --- | --- | --- | --- |
| Windows 7 Ultimate SP1 x64 | Modern, 2 GiB RAM, 32 GiB virtual disk | Completed to desktop | Passed: saved disk returns to desktop |
| Geek XP 41 | Classic, 512 MiB RAM, 8 GiB virtual disk | Completed to desktop | Passed: saved disk returns to desktop |

The XP image is modified installation media. Its result cannot establish stock
XP compatibility. No activation, update-service, network, or application suite
validation is included. These are virtual-machine tests, not bare-metal PXE
installation results.

The modified XP image's bundled Firefox installer required SSE2, which the
Pentium III preset lacks. Cancelling that optional application installer allowed
first-login setup to finish. The OS reached its desktop with the unchanged
Classic preset; bundled application compatibility is not claimed.

Windows 7 used a disposable local account, skipped the offered product-key step,
and reached the desktop without activation. Both guests shut down through ACPI
and their saved QCOW2 disks passed `qemu-img check` before new probe/QEMU
processes booted them directly to their desktops. The installer did not restart.

At the installed desktops, QMP reported these cumulative guest I/O counters:

| Image | Streamed CD bytes read | Virtual disk bytes written | Read/write errors |
| --- | ---: | ---: | ---: |
| Windows 7 | 3,685,173,248 | 8,742,798,848 | 0 |
| Geek XP 41 | 772,829,308 | 2,621,313,024 | 0 |

Source writes were zero. These are cumulative transfers, including rereads,
not unique bytes or memory usage. Both ISO files exceed their guest RAM
allocation; no full-ISO guest RAM preload was used.

## Controls and streaming integrity

Initial tests encountered a `driver.cab` copy failure in XP and a missing-CD
message in Windows 7 Setup. The temporary server copy became unavailable and
HTTP subsequently returned 404; the exact cause of that removal was not
established. Those runs are inconclusive and are not counted as successful
installations. The original ISOs were unaffected.

A directly attached local-ISO control passed XP's file-copy phase and reached
graphical Setup; the Windows 7 local control reached the license screen.
The XP `DRIVER.CAB` independently passed 7-Zip's archive integrity test. Both
streaming tests were restarted with retained server directories outside the
system temporary folder. They then passed the earlier failure points.

An independent `qemu-img convert` read of the complete Windows 7 image through
the live Everboot NBD bridge matched the original ISO's SHA-256:

```text
36F4FA2416D0982697AB106E3A72D2E120DBCDB6CC54FD3906D06120D0653808
```

That full-image transfer was an explicit integrity test written to disk, not
the launcher's boot behavior. The temporary comparison output was removed
after verification. Normal VM reads continue through bounded transfer buffers.

## Boot profiles and checks

- The supplied Serenity image reached its graphical desktop using a server-side
  profile, staged kernel/initrd, and a streamed disk with a writable overlay.
  No diagnostic QEMU arguments were needed.
- A CD/startup-floppy profile attached the Windows 98 CD alongside FreeDOS 1.4
  and reached the FreeDOS startup UI. Windows 98 Setup remains unverified;
  a compatible Windows 98 startup disk is still needed for that test.
- Missing startup files were verified in Chromium: the affected image shows a
  repair message without Start; fixing the profile and refreshing restores
  the normal Start button and preset.
- **492 integration checks passed on Windows and WSL Linux**, including profile
  validation, asset transfer/checksums, cache repair, changed-asset rejection,
  cleanup, and existing streaming protocols.
- Actual QEMU overlay write/reopen checks passed on Windows and WSL Linux.
- Release builds passed with warnings treated as errors. The self-contained
  Windows executable was republished and its invalid-URL usage path checked.

Configuration, limits, and the persistent probe workflow are documented in
[VM boot profiles](vm-boot-profiles.md). Screenshots, QMP counters, saved test
disks, and verification notes are ignored local files in `artifacts/install-tests`.
No operating system binaries or test screenshots are committed.

## Remaining work

- **Recommended next action; owner: test maintainer (TBD).** Provide a compatible Windows 98 startup disk
  and validate its installer with the new pairing mechanism.
- **Owner: maintainer.** Test a separate physical server/client network, network
  interruption/recovery, and additional stock legacy installation media.
