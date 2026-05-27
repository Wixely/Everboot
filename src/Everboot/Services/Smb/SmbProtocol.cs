namespace Everboot.Services.Smb;

/// <summary>
/// SMB2 commands (MS-SMB2 §2.2.1.2 Command field). Read-only subset.
/// </summary>
internal enum Smb2Command : ushort
{
    Negotiate = 0x0000,
    SessionSetup = 0x0001,
    Logoff = 0x0002,
    TreeConnect = 0x0003,
    TreeDisconnect = 0x0004,
    Create = 0x0005,
    Close = 0x0006,
    Flush = 0x0007,
    Read = 0x0008,
    Write = 0x0009,
    Lock = 0x000A,
    Ioctl = 0x000B,
    Cancel = 0x000C,
    Echo = 0x000D,
    QueryDirectory = 0x000E,
    ChangeNotify = 0x000F,
    QueryInfo = 0x0010,
    SetInfo = 0x0011,
    OplockBreak = 0x0012,
}

internal static class Smb2HeaderFlags
{
    public const uint ServerToRedir = 0x00000001;
    public const uint AsyncCommand = 0x00000002;
    public const uint RelatedOperations = 0x00000004;
    public const uint Signed = 0x00000008;
    public const uint DfsOperations = 0x10000000;
}

/// <summary>
/// NTSTATUS values we actually return.
/// </summary>
internal static class NtStatus
{
    public const uint Success = 0x00000000;
    public const uint Pending = 0x00000103;
    public const uint NoMoreFiles = 0x80000006;

    public const uint EndOfFile = 0xC0000011;
    public const uint InvalidParameter = 0xC000000D;
    public const uint AccessDenied = 0xC0000022;
    public const uint ObjectNameNotFound = 0xC0000034;
    public const uint ObjectNameCollision = 0xC0000035;
    public const uint ObjectPathNotFound = 0xC000003A;
    public const uint InvalidDeviceRequest = 0xC0000010;
    public const uint NotSupported = 0xC00000BB;
    public const uint InvalidSmb = 0xC00000C9;
    public const uint BadNetworkName = 0xC00000CC;
    public const uint FileIsADirectory = 0xC00000BA;
    public const uint NotADirectory = 0xC0000103;
    public const uint UserSessionDeleted = 0xC0000203;
    public const uint NetworkNameDeleted = 0xC00000C9;
    public const uint MoreProcessingRequired = 0xC0000016;
    public const uint FsDriverRequired = 0xC000019C;
    public const uint InvalidInfoClass = 0xC0000003;
    public const uint BufferTooSmall = 0xC0000023;
    public const uint BufferOverflow = 0x80000005;
}

/// <summary>
/// File attribute flags returned in CREATE/QUERY responses.
/// </summary>
internal static class FileAttributes
{
    public const uint ReadOnly = 0x00000001;
    public const uint Hidden = 0x00000002;
    public const uint System = 0x00000004;
    public const uint Directory = 0x00000010;
    public const uint Archive = 0x00000020;
    public const uint Normal = 0x00000080;
}

/// <summary>
/// SMB2 dialect revisions we know about. We only respond with 2.0.2.
/// </summary>
internal static class Smb2Dialect
{
    public const ushort Smb202 = 0x0202;
    public const ushort Smb210 = 0x0210;
    public const ushort Smb300 = 0x0300;
    public const ushort Smb302 = 0x0302;
    public const ushort Smb311 = 0x0311;
    public const ushort Smb2Wildcard = 0x02FF;
}

/// <summary>
/// FileInformation class values for QUERY_DIRECTORY / QUERY_INFO.
/// </summary>
internal static class FileInfoClass
{
    public const byte FileDirectoryInformation = 0x01;
    public const byte FileFullDirectoryInformation = 0x02;
    public const byte FileBothDirectoryInformation = 0x03;
    public const byte FileBasicInformation = 0x04;
    public const byte FileStandardInformation = 0x05;
    public const byte FileInternalInformation = 0x06;
    public const byte FileEaInformation = 0x07;
    public const byte FileAccessInformation = 0x08;
    public const byte FileNamesInformation = 0x0C;
    public const byte FilePositionInformation = 0x0E;
    public const byte FileFullEaInformation = 0x0F;
    public const byte FileModeInformation = 0x10;
    public const byte FileAlignmentInformation = 0x11;
    public const byte FileAllInformation = 0x12;
    public const byte FileNetworkOpenInformation = 0x22;
    public const byte FileAttributeTagInformation = 0x23;
    public const byte FileIdBothDirectoryInformation = 0x25;
    public const byte FileIdFullDirectoryInformation = 0x26;
}

internal static class FileSystemInfoClass
{
    public const byte FileFsVolumeInformation = 0x01;
    public const byte FileFsSizeInformation = 0x03;
    public const byte FileFsDeviceInformation = 0x04;
    public const byte FileFsAttributeInformation = 0x05;
    public const byte FileFsFullSizeInformation = 0x07;
}

internal static class InfoType
{
    public const byte File = 0x01;
    public const byte FileSystem = 0x02;
    public const byte Security = 0x03;
    public const byte Quota = 0x04;
}

internal static class ShareType
{
    public const byte Disk = 0x01;
    public const byte Pipe = 0x02;
    public const byte Print = 0x03;
}
