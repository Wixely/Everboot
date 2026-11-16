using System.Buffers.Binary;
using System.Text;

namespace Everboot.Services.Vm;

internal static class DosInstallDisk
{
    // Only called for a newly created private VM disk, never an existing disk.
    // A roughly 2 GiB FAT16 partition with 32 KiB clusters (65,511 clusters).
    internal const int PartitionSectors = 4193280;
    internal static void Create(string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        output.SetLength(2L << 30);
        var mbr = new byte[512];
        using (var resource = typeof(DosInstallDisk).Assembly.GetManifestResourceStream("Everboot.DosMbr")
            ?? throw new IOException("Everboot MBR is missing.")) resource.ReadExactly(mbr);
        mbr[446] = 0x80; mbr[447] = 1; mbr[448] = 1;
        mbr[450] = 0x0e; mbr[451] = 0xfe; mbr[452] = 0xff; mbr[453] = 0xff;
        BinaryPrimitives.WriteInt32LittleEndian(mbr.AsSpan(454), 63);
        BinaryPrimitives.WriteInt32LittleEndian(mbr.AsSpan(458), PartitionSectors);
        output.Write(mbr);
        var boot = new byte[512];
        boot[0] = 0xeb; boot[1] = 0x3c; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("EVERBOOT").CopyTo(boot, 3);
        void Word(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(offset), value);
        void Dword(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(boot.AsSpan(offset), value);
        Word(11, 512); boot[13] = 64; Word(14, 1); boot[16] = 2; Word(17, 512);
        boot[21] = 0xf8; Word(22, 256); Word(24, 63); Word(26, 16);
        Dword(28, 63); Dword(32, PartitionSectors); boot[36] = 0x80; boot[38] = 0x29;
        Dword(39, 0x45564231);
        Encoding.ASCII.GetBytes("EVERBOOT   FAT16   ").CopyTo(boot, 43);
        // Until Setup transfers its system files, ask BIOS to try the next device.
        boot[62] = 0xcd; boot[63] = 0x18; boot[64] = 0xeb; boot[65] = 0xfc;
        Word(510, 0xaa55);
        output.Position = 63L * 512; output.Write(boot);
        byte[] fat = [0xf8, 0xff, 0xff, 0xff];
        output.Position = 64L * 512; output.Write(fat);
        output.Position = (64L + 256) * 512; output.Write(fat);
        output.Flush(true);
    }
}
