using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Everboot.Services.Nfs;

internal sealed record NfsNode(uint Id, string Path, string Name, bool Directory, ulong Size, uint Parent);

// A mounted image is held read-only. Handles are generation-scoped: replacing an
// ISO invalidates the old export instead of serving new bytes under old handles.
internal sealed class NfsExport : IDisposable
{
    private const int MaxNodes = 100_000;
    private readonly FileStream _image;
    private readonly DiscFileSystem _fs;
    private readonly Dictionary<uint, NfsNode> _nodes = new();
    private readonly Dictionary<uint, NfsNode[]> _children = new();
    public Guid Generation { get; } = Guid.NewGuid();
    public IsoEntry Entry { get; }
    public long Stamp { get; }
    public uint Timestamp { get; }
    public NfsNode Root => _nodes[1];

    public NfsExport(IsoEntry entry)
    {
        Entry = entry;
        Stamp = File.GetLastWriteTimeUtc(entry.FullPath).Ticks;
        Timestamp = (uint)Math.Clamp(new DateTimeOffset(new DateTime(Stamp, DateTimeKind.Utc)).ToUnixTimeSeconds(), 0, uint.MaxValue);
        _image = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.RandomAccess);
        try
        {
            _fs = entry.Metadata.Layout.HasFlag(IsoLayout.Iso9660)
                ? new CDReader(_image, joliet: true) : new UdfReader(_image);
            _nodes.Add(1, new(1, "\\", "", true, 4096, 1));
        }
        catch { _image.Dispose(); throw; }
    }

    public bool IsCurrent(IsoEntry entry) => Entry.FullPath == entry.FullPath &&
        Entry.SizeBytes == entry.SizeBytes && Stamp == File.GetLastWriteTimeUtc(entry.FullPath).Ticks;

    public byte[] Handle(NfsNode node)
    {
        var w = new XdrWriter();
        w.Fixed(Generation.ToByteArray());
        w.UInt(node.Id);
        return w.ToArray();
    }

    public NfsNode? Resolve(byte[] handle)
    {
        if (handle.Length != 20 || new Guid(handle.AsSpan(0, 16)) != Generation) return null;
        return _nodes.GetValueOrDefault(new XdrReader(handle[16..]).UInt());
    }

    public NfsNode[] Children(NfsNode parent)
    {
        if (!parent.Directory) throw new IOException("Not a directory.");
        if (_children.TryGetValue(parent.Id, out var cached)) return cached;
        var paths = _fs.GetDirectories(parent.Path).Select(p => (Path: p, Directory: true))
            .Concat(_fs.GetFiles(parent.Path).Select(p => (Path: p, Directory: false)))
            .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
        if (paths.Length > MaxNodes - _nodes.Count) throw new IOException("Image directory limit exceeded.");
        var children = new List<NfsNode>();
        foreach (var path in paths)
        {
            var name = path.Path.TrimEnd('\\', '/').Split('\\', '/')[^1];
            // DiscUtils may expose ISO9660 version suffixes. Linux sees normal filenames.
            if (name.EndsWith(";1", StringComparison.Ordinal)) name = name[..^2];
            if (name is "." or ".." || name.Length == 0) continue;
            var node = new NfsNode((uint)_nodes.Count + 1, path.Path, name, path.Directory,
                path.Directory ? 4096ul : checked((ulong)_fs.GetFileLength(path.Path)), parent.Id);
            _nodes.Add(node.Id, node);
            children.Add(node);
        }
        return _children[parent.Id] = children.ToArray();
    }

    public NfsNode? Lookup(NfsNode parent, string name)
    {
        if (name == ".") return parent;
        if (name == "..") return _nodes[parent.Parent];
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', '\0']) >= 0) return null;
        return Children(parent).FirstOrDefault(n => n.Name == name);
    }

    public byte[] Read(NfsNode node, ulong offset, uint count)
    {
        if (node.Directory) throw new IOException("Cannot read a directory.");
        if (offset >= node.Size) return [];
        var length = (int)Math.Min(Math.Min(count, NfsProtocol.MaxRead), node.Size - offset);
        using var file = _fs.OpenFile(node.Path, FileMode.Open, FileAccess.Read);
        file.Position = checked((long)offset);
        var bytes = new byte[length];
        file.ReadExactly(bytes);
        return bytes;
    }

    public void Attributes(XdrWriter w, NfsNode node)
    {
        w.UInt(node.Directory ? 2u : 1u); // NF3DIR / NF3REG
        w.UInt(node.Directory ? 365u : 292u); // 0555 / 0444
        w.UInt(node.Directory ? 2u : 1u); // nlink
        w.UInt(0); w.UInt(0); // uid / gid
        w.ULong(node.Size); w.ULong(node.Size);
        w.UInt(0); w.UInt(0); // rdev
        w.ULong(BitConverter.ToUInt64(Generation.ToByteArray()));
        w.ULong(node.Id);
        for (var i = 0; i < 3; i++) { w.UInt(Timestamp); w.UInt(0); }
    }
    public void PostAttributes(XdrWriter w, NfsNode node) { w.Bool(true); Attributes(w, node); }
    public void Dispose() { _fs.Dispose(); _image.Dispose(); }
}
