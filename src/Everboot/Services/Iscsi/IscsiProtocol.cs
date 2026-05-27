namespace Everboot.Services.Iscsi;

/// <summary>
/// iSCSI PDU opcodes (RFC 7143 §11). Bits 6-7 of the opcode byte are reserved
/// for the I (Immediate) flag and PDU-type direction; here we just enumerate
/// the low-6-bit opcode value.
/// </summary>
internal enum IscsiOpcode : byte
{
    NopOut = 0x00,
    ScsiCommand = 0x01,
    ScsiTaskMgmtRequest = 0x02,
    LoginRequest = 0x03,
    TextRequest = 0x04,
    ScsiDataOut = 0x05,
    LogoutRequest = 0x06,
    SnackRequest = 0x10,

    NopIn = 0x20,
    ScsiResponse = 0x21,
    ScsiTaskMgmtResponse = 0x22,
    LoginResponse = 0x23,
    TextResponse = 0x24,
    ScsiDataIn = 0x25,
    LogoutResponse = 0x26,
    ReadyToTransfer = 0x31,
    AsyncMessage = 0x32,
    Reject = 0x3F,
}

internal static class IscsiBhsFlags
{
    public const byte Final = 0x80;        // F bit
    public const byte Continue = 0x40;     // C bit
    public const byte Transit = 0x80;      // T bit (login)
}

/// <summary>
/// SCSI status codes returned in the iSCSI SCSI Response PDU.
/// </summary>
internal enum ScsiStatus : byte
{
    Good = 0x00,
    CheckCondition = 0x02,
    ConditionMet = 0x04,
    Busy = 0x08,
    ReservationConflict = 0x18,
}

/// <summary>
/// SCSI opcodes - the subset we accept. Reads + a few harmless metadata
/// queries. Everything else gets CHECK CONDITION / Invalid Operation.
/// </summary>
internal static class ScsiOpcode
{
    public const byte TestUnitReady = 0x00;
    public const byte RequestSense = 0x03;
    public const byte Inquiry = 0x12;
    public const byte ModeSense6 = 0x1A;
    public const byte StartStopUnit = 0x1B;
    public const byte PreventAllowMediumRemoval = 0x1E;
    public const byte ReadCapacity10 = 0x25;
    public const byte Read10 = 0x28;
    public const byte Read12 = 0xA8;
    public const byte Read16 = 0x88;
    public const byte ServiceActionIn16 = 0x9E; // ReadCapacity16 lives here
    public const byte ModeSense10 = 0x5A;
    public const byte ReportLuns = 0xA0;
    public const byte Verify10 = 0x2F;
    public const byte SynchronizeCache10 = 0x35;
    public const byte ReadTocPmaAtip = 0x43; // CD-only
    public const byte GetConfiguration = 0x46; // CD-only
    public const byte GetEventStatus = 0x4A;
    public const byte ReadDiscInformation = 0x51;
}

/// <summary>
/// SCSI sense keys + ASC/ASCQ pairs we return for the handful of failure
/// cases we surface.
/// </summary>
internal static class ScsiSense
{
    public const byte NoSense = 0x00;
    public const byte NotReady = 0x02;
    public const byte MediumError = 0x03;
    public const byte IllegalRequest = 0x05;
    public const byte UnitAttention = 0x06;
    public const byte DataProtect = 0x07;

    // ASC/ASCQ pairs (high byte ASC, low byte ASCQ)
    public const ushort InvalidCommandOperationCode = 0x2000;
    public const ushort InvalidFieldInCdb = 0x2400;
    public const ushort LbaOutOfRange = 0x2100;
    public const ushort WriteProtected = 0x2700;
}

internal enum IscsiSessionType
{
    Discovery,
    Normal,
}
