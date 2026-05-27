namespace Everboot.Services;

internal enum DhcpOp : byte
{
    BootRequest = 1,
    BootReply = 2,
}

internal enum DhcpMessageType : byte
{
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8,
}

/// <summary>
/// DHCP option codes we look at or emit. There are dozens more; this is just
/// what the PXE proxy cares about.
/// </summary>
internal static class DhcpOption
{
    public const byte Pad = 0;
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte HostName = 12;
    public const byte VendorSpecific = 43;
    public const byte MessageType = 53;
    public const byte ServerIdentifier = 54;
    public const byte ParameterRequestList = 55;
    public const byte MaximumMessageSize = 57;
    public const byte VendorClassIdentifier = 60;
    public const byte ClientIdentifier = 61;
    public const byte TftpServerName = 66;
    public const byte BootFileName = 67;
    public const byte UserClass = 77;
    public const byte ClientArchitecture = 93;
    public const byte ClientNetworkInterface = 94;
    public const byte ClientMachineId = 97;
    public const byte End = 255;
}

/// <summary>
/// Client architecture types per RFC 4578 / IANA. The PXE firmware advertises
/// one of these in option 93 so we can pick the right bootloader to hand back.
/// </summary>
internal static class DhcpClientArch
{
    public const ushort IntelX86Pc = 0;          // legacy BIOS
    public const ushort EfiIa32 = 6;             // 32-bit UEFI
    public const ushort EfiX64 = 7;              // 64-bit UEFI
    public const ushort EfiBc = 9;               // EFI byte code, treated as x64
    public const ushort EfiArm32 = 10;
    public const ushort EfiArm64 = 11;
    public const ushort HttpX64 = 16;            // UEFI HTTP boot x64
    public const ushort HttpIa32 = 15;
    public const ushort HttpArm64 = 19;
}

internal enum DhcpClientFamily
{
    Bios,
    Uefi32,
    Uefi64,
    UefiArm64,
}
