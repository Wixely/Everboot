using System.Buffers.Binary;
using System.Text;
using DiscUtils;

namespace Everboot.Services;

// Read-only FAT16 projection of WIN98's short-name setup files. Only filesystem
// metadata is synthesized in memory; file payloads stay in the original ISO.
internal sealed class DosSourceDisk
{
    public const long Length = 2L << 30;
    private const int ClusterBytes = 32768;
    private const int DataStart = 608 * 512;
    private readonly byte[] _metadata = new byte[DataStart + ClusterBytes];
    private readonly List<(long Start, long Length, string Path)> _files = [];

    public DosSourceDisk(DiscFileSystem fs)
    {
        if (IsoFileResolver.Resolve(fs, "win98/setup.exe") is null) throw new IOException("Windows 98 Setup missing.");
        var directory = fs.GetDirectories("\\").SingleOrDefault(p =>
            p.Replace('\\', '/').TrimEnd('/').Split('/')[^1].Split(';')[0].Equals("win98", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("WIN98 directory missing.");
        var mbr = _metadata.AsSpan(0, 512);
        mbr[446] = 0x80; mbr[447] = 1; mbr[448] = 1; mbr[450] = 0x0e;
        mbr[451] = 0xfe; mbr[452] = 0xff; mbr[453] = 0xff;
        Dword(mbr, 454, 63); Dword(mbr, 458, 4193280); Word(mbr, 510, 0xaa55);
        var boot = _metadata.AsSpan(63 * 512, 512);
        boot[0] = 0xeb; boot[1] = 0x3c; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("EVERBOOT").CopyTo(boot[3..]);
        Word(boot, 11, 512); boot[13] = 64; Word(boot, 14, 1); boot[16] = 2;
        Word(boot, 17, 512); boot[21] = 0xf8; Word(boot, 22, 256);
        Word(boot, 24, 63); Word(boot, 26, 16); Dword(boot, 28, 63);
        Dword(boot, 32, 4193280); boot[36] = 0x80; boot[38] = 0x29;
        Dword(boot, 39, 0x45564232);
        Encoding.ASCII.GetBytes("EVBSOURCE  FAT16   ").CopyTo(boot[43..]);
        boot[62] = 0xcd; boot[63] = 0x18; boot[64] = 0xeb; boot[65] = 0xfc;
        Word(boot, 510, 0xaa55);
        var fat = _metadata.AsSpan(64 * 512, 256 * 512);
        Word(fat, 0, 0xfff8); Word(fat, 2, 0xffff); Word(fat, 4, 0xffff);
        Entry(_metadata.AsSpan(576 * 512, 32), "WIN98", 0x10, 2, 0);
        var listing = _metadata.AsSpan(DataStart, ClusterBytes);
        Entry(listing[..32], ".", 0x10, 2, 0);
        Entry(listing.Slice(32, 32), "..", 0x10, 0, 0);
        var cluster = 3;
        var index = 2;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in fs.GetFiles(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = path.Replace('\\', '/').Split('/')[^1].Split(';')[0].ToUpperInvariant();
            if (!names.Add(name)) throw new IOException("Duplicate DOS setup filename.");
            var size = fs.GetFileLength(path);
            var count = checked((int)((size + ClusterBytes - 1) / ClusterBytes));
            if (size < 0 || size > uint.MaxValue || cluster + count > 65513 || index >= 1024)
                throw new IOException("Setup source exceeds the DOS projection capacity.");
            Entry(listing.Slice(index++ * 32, 32), name, 0x21, size == 0 ? 0 : cluster, size);
            _files.Add((DataStart + (cluster - 2L) * ClusterBytes, size, path));
            for (var n = 0; n < count; n++, cluster++) Word(fat, cluster * 2, n == count - 1 ? 0xffff : cluster + 1);
        }
        fat.CopyTo(_metadata.AsSpan(320 * 512));
    }

    public void Read(DiscFileSystem fs, long position, Span<byte> buffer)
    {
        if (position < 0 || position > Length || buffer.Length > Length - position) throw new ArgumentOutOfRangeException(nameof(position));
        buffer.Clear();
        var end = position + buffer.Length;
        if (position < _metadata.Length)
            _metadata.AsSpan((int)position, (int)Math.Min(buffer.Length, _metadata.Length - position)).CopyTo(buffer);
        foreach (var file in _files)
        {
            var start = Math.Max(position, file.Start);
            var stop = Math.Min(end, file.Start + file.Length);
            if (stop <= start) continue;
            using var input = fs.OpenFile(file.Path, FileMode.Open, FileAccess.Read);
            input.Position = start - file.Start;
            input.ReadExactly(buffer.Slice((int)(start - position), (int)(stop - start)));
        }
    }

    private static void Entry(Span<byte> entry, string name, byte attributes, int cluster, long size)
    {
        string encoded;
        if (name is "." or "..") encoded = name.PadRight(11);
        else
        {
            var parts = name.Split('.');
            if (parts.Length > 2 || parts[0].Length is < 1 or > 8 || (parts.Length == 2 && parts[1].Length is < 1 or > 3) ||
                name.Any(c => !(char.IsAsciiLetterOrDigit(c) || "._-$~!#%&'()@^`{}".Contains(c))))
                throw new IOException("Setup source requires an unsupported DOS filename: " + name);
            encoded = parts[0].PadRight(8) + (parts.Length == 2 ? parts[1] : "").PadRight(3);
        }
        Encoding.ASCII.GetBytes(encoded).CopyTo(entry);
        entry[11] = attributes; Word(entry, 24, 0x2821); // 2000-01-01, deterministic DOS date
        Word(entry, 26, cluster); Dword(entry, 28, checked((uint)size));
    }
    private static void Word(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], checked((ushort)value));
    private static void Dword(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
}
