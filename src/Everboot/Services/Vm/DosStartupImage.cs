using System.Buffers.Binary;
using System.Text;

namespace Everboot.Services.Vm;

// A fixed FAT12 filesystem generated from files supplied by the user's CD.
// The only embedded executable bytes are our own 512-byte bootstrap.
internal static class DosStartupImage
{
    internal static byte[] Build(IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("IO.SYS", out var io) || io.Length < 2048 ||
            io[0] != 'M' || io[1] != 'Z' || io[512] != 'B' || io[513] != 'J' ||
            io[2046] != 'M' || io[2047] != 'S')
            throw new IOException("The CD does not contain the supported DOS 7.1 loader.");
        var disk = new byte[1474560];
        using (var boot = typeof(DosStartupImage).Assembly.GetManifestResourceStream("Everboot.Dos98Boot")
            ?? throw new IOException("Everboot bootstrap is missing.")) boot.ReadExactly(disk.AsSpan(0, 512));
        var fat = new byte[4608]; fat[0] = 0xf0; fat[1] = fat[2] = 0xff;
        var cluster = 2;
        var entry = 19 * 512;
        // IO.SYS must be first and contiguous: the bootstrap reads its first four sectors.
        foreach (var name in new[] { "IO.SYS" }.Concat(files.Keys.Where(n => n != "IO.SYS")))
        {
            var parts = name.Split('.');
            if (parts.Length != 2 || parts[0].Length is < 1 or > 8 || parts[1].Length is < 1 or > 3 ||
                name.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.')))
                throw new IOException("Invalid DOS startup filename.");
            var bytes = files[name];
            var count = (bytes.Length + 511L) / 512;
            if (entry >= 33 * 512 || cluster + count > 2849) throw new IOException("DOS startup files exceed the boot disk capacity.");
            Encoding.ASCII.GetBytes(parts[0].PadRight(8) + parts[1].PadRight(3)).CopyTo(disk, entry);
            disk[entry + 11] = name is "IO.SYS" or "MSDOS.SYS" ? (byte)7 : (byte)32;
            BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(entry + 26), bytes.Length == 0 ? (ushort)0 : (ushort)cluster);
            BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(entry + 28), bytes.Length);
            bytes.CopyTo(disk, (33 + cluster - 2) * 512);
            for (var j = 0; j < count; j++, cluster++)
            {
                var next = j == count - 1 ? 0xfff : cluster + 1;
                var offset = cluster * 3 / 2;
                if ((cluster & 1) == 0)
                {
                    fat[offset] = (byte)next;
                    fat[offset + 1] = (byte)((fat[offset + 1] & 0xf0) | (next >> 8));
                }
                else
                {
                    fat[offset] = (byte)((fat[offset] & 0x0f) | (next << 4));
                    fat[offset + 1] = (byte)(next >> 4);
                }
            }
            entry += 32;
        }
        fat.CopyTo(disk, 512); fat.CopyTo(disk, 5120);
        return disk;
    }
}
