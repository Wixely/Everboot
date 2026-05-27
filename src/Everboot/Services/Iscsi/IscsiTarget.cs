using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Everboot.Services.Iscsi;

/// <summary>
/// One iSCSI target = one image file exposed as a SCSI block device.
/// Targets are read-only. The catalog (re)builds the list as images come
/// and go from <c>data/isos/</c>; each entry maps to a stable IQN derived
/// from its slug.
/// </summary>
internal sealed class IscsiTarget
{
    private const int CdRomBlockSize = 2048;
    private const int DiskBlockSize = 512;

    public string Iqn { get; }
    public string FilePath { get; }
    public long FileLength { get; }
    public int BlockSize { get; }
    public byte PeripheralDeviceType { get; }   // 0 = direct access, 5 = CD-ROM
    public bool Removable { get; }
    public string DisplayName { get; }

    public long LastLba => Math.Max(0L, (FileLength / BlockSize) - 1L);

    private IscsiTarget(
        string iqn,
        string filePath,
        long fileLength,
        int blockSize,
        byte peripheralDeviceType,
        bool removable,
        string displayName)
    {
        Iqn = iqn;
        FilePath = filePath;
        FileLength = fileLength;
        BlockSize = blockSize;
        PeripheralDeviceType = peripheralDeviceType;
        Removable = removable;
        DisplayName = displayName;
    }

    public static IscsiTarget ForIso(string iqn, string filePath, long length, string displayName)
        => new(iqn, filePath, length, CdRomBlockSize, 0x05, removable: true, displayName);

    public static IscsiTarget ForDisk(string iqn, string filePath, long length, string displayName)
        => new(iqn, filePath, length, DiskBlockSize, 0x00, removable: false, displayName);

    public FileStream OpenRead() => new(
        FilePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.RandomAccess);

    /// <summary>
    /// Build a standard INQUIRY response (36 bytes). Vendor/product strings
    /// are space-padded to fixed widths per SPC-4 §6.4.2.
    /// </summary>
    public byte[] BuildStandardInquiry()
    {
        var buf = new byte[36];
        buf[0] = PeripheralDeviceType;
        buf[1] = (byte)(Removable ? 0x80 : 0x00);
        buf[2] = 0x05;          // SPC-3
        buf[3] = 0x02;          // response data format 2, NACA/HiSup off
        buf[4] = 31;            // additional length = total - 5

        WriteSpacePadded(buf.AsSpan(8, 8), "EVERBOOT");
        WriteSpacePadded(buf.AsSpan(16, 16), DisplayName);
        WriteSpacePadded(buf.AsSpan(32, 4), "1.0");

        return buf;
    }

    public byte[] BuildReadCapacity10()
    {
        var buf = new byte[8];
        var last = (uint)Math.Min(LastLba, uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), last);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)BlockSize);
        return buf;
    }

    public byte[] BuildReadCapacity16()
    {
        var buf = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0, 8), (ulong)LastLba);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)BlockSize);
        // rest stays zero (no protection, no logical-block exponents)
        return buf;
    }

    /// <summary>
    /// Build a minimal MODE SENSE(6) response for a write-protected medium.
    /// </summary>
    public byte[] BuildModeSense6()
    {
        // 4-byte header: mode data length, medium type, device-specific, block descriptor length.
        var buf = new byte[4];
        buf[0] = 3;  // mode data length = remaining 3 bytes
        buf[1] = 0x00;
        buf[2] = 0x80; // WP = 1 -> write-protected
        buf[3] = 0;
        return buf;
    }

    public byte[] BuildReportLuns()
    {
        // We expose exactly one LUN (0).
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 8); // LUN list length
        // bytes 4-7 reserved (zero)
        // bytes 8-15 = LUN 0 (all zero)
        return buf;
    }

    private static void WriteSpacePadded(Span<byte> destination, string source)
    {
        destination.Fill(0x20);
        var bytes = Encoding.ASCII.GetBytes(source);
        var copy = Math.Min(bytes.Length, destination.Length);
        bytes.AsSpan(0, copy).CopyTo(destination);
    }
}
