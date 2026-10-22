using Everboot.Configuration;

namespace Everboot.Services.Nfs;

/// <summary>Read-only NFSv3, MOUNTv3 and portmapper v2 over ONC RPC (RFC 1813/1833).
/// The lock protects DiscUtils readers and generation-scoped handle tables.</summary>
internal sealed class NfsProtocol(BootCatalog catalog, NfsOptions options, NfsStatus? status = null) : IDisposable
{
    public const uint MaxRead = 64 * 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NfsExport> _exports = new(StringComparer.Ordinal);

    public byte[] Dispatch(byte[] record, uint expectedProgram)
    {
        lock (_gate)
        {
            var r = new XdrReader(record);
            var xid = r.UInt();
            try
            {
                if (r.UInt() != 0) throw new InvalidDataException("Expected RPC call.");
                if (r.UInt() != 2)
                {
                    var denied = new XdrWriter();
                    denied.UInt(xid); denied.UInt(1); denied.UInt(1); denied.UInt(0); denied.UInt(2); denied.UInt(2);
                    return denied.ToArray();
                }
                var program = r.UInt(); var version = r.UInt(); var procedure = r.UInt();
                r.UInt(); r.Opaque(400); // AUTH_NONE / AUTH_SYS credentials; read-only public exports
                r.UInt(); r.Opaque(400); // verifier
                if (program != expectedProgram) return Reply(xid, 1).ToArray(); // PROG_UNAVAIL
                var wantedVersion = program == 100000 ? 2u : 3u;
                if (version != wantedVersion)
                {
                    var mismatch = Reply(xid, 2);
                    mismatch.UInt(wantedVersion); mismatch.UInt(wantedVersion);
                    return mismatch.ToArray();
                }
                var w = Reply(xid, 0);
                if (procedure == 0) return w.ToArray();
                Refresh();
                var handled = program switch
                {
                    100000 => Portmap(r, w, procedure),
                    100005 => Mount(r, w, procedure),
                    100003 => Nfs(r, w, procedure),
                    _ => false,
                };
                return handled ? w.ToArray() : Reply(xid, 3).ToArray(); // PROC_UNAVAIL
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or System.Text.DecoderFallbackException or OverflowException)
            {
                return Reply(xid, 4).ToArray(); // GARBAGE_ARGS
            }
        }
    }

    private static XdrWriter Reply(uint xid, uint status)
    {
        var w = new XdrWriter();
        w.UInt(xid); w.UInt(1); w.UInt(0); // REPLY / MSG_ACCEPTED
        w.UInt(0); w.UInt(0); // AUTH_NONE verifier
        w.UInt(status);
        return w;
    }

    private void Refresh()
    {
        foreach (var pair in _exports.ToArray())
        {
            if (!catalog.TryGetBySlug(pair.Key, out var entry) || entry is null || !pair.Value.IsCurrent(entry))
            {
                pair.Value.Dispose();
                _exports.Remove(pair.Key);
            }
        }
    }

    private bool Portmap(XdrReader r, XdrWriter w, uint procedure)
    {
        if (procedure == 3) // GETPORT; UDP is deliberately not advertised
        {
            var program = r.UInt(); var version = r.UInt(); var protocol = r.UInt(); r.UInt();
            w.UInt(protocol != 6 ? 0 : (program, version) switch
            {
                (100000, 2) => (uint)options.PortmapperPort,
                (100003, 3) => (uint)options.Port,
                (100005, 3) => (uint)options.MountPort,
                _ => 0,
            });
            return true;
        }
        if (procedure == 4) // DUMP
        {
            foreach (var (program, version, port) in new[] { (100000u, 2u, options.PortmapperPort), (100003u, 3u, options.Port), (100005u, 3u, options.MountPort) })
            { w.Bool(true); w.UInt(program); w.UInt(version); w.UInt(6); w.UInt((uint)port); }
            w.Bool(false);
            return true;
        }
        return false;
    }

    private bool Mount(XdrReader r, XdrWriter w, uint procedure)
    {
        if (procedure == 1)
        {
            var path = r.Text(1024);
            var slug = path.StartsWith('/') ? path[1..].TrimEnd('/') : "";
            if (!catalog.TryGetBySlug(slug, out var entry) || entry is null || entry.Metadata.Layout == IsoLayout.None)
            { w.UInt(2); return true; }
            try
            {
                if (!_exports.TryGetValue(slug, out var export)) _exports.Add(slug, export = new NfsExport(entry));
                w.UInt(0); w.Opaque(export.Handle(export.Root));
                w.UInt(2); w.UInt(0); w.UInt(1); // AUTH_NONE and AUTH_SYS
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { w.UInt(5); }
            return true;
        }
        if (procedure == 2) { w.Bool(false); return true; } // no persisted mount list
        if (procedure == 3) { r.Text(1024); return true; } // UMNT: handles remain valid
        if (procedure == 4) return true;
        if (procedure == 5)
        {
            foreach (var entry in catalog.Entries.Where(e => e.Metadata.Layout != IsoLayout.None))
            { w.Bool(true); w.Text("/" + entry.Slug); w.Bool(false); }
            w.Bool(false); return true;
        }
        return false;
    }

    private bool Nfs(XdrReader r, XdrWriter w, uint procedure)
    {
        if (procedure > 21) return false;
        // Mutation operations always fail. Failure unions differ by procedure.
        if (procedure is 2 or >= 7 and <= 15 or 21)
        {
            w.UInt(30); // NFS3ERR_ROFS
            if (procedure == 15) w.Bool(false); // LINK post_op_attr
            w.Bool(false); w.Bool(false); // wcc_data
            if (procedure == 14) { w.Bool(false); w.Bool(false); }
            return true;
        }
        var handle = r.Opaque(64);
        var export = _exports.Values.FirstOrDefault(e => e.Resolve(handle) is not null);
        var node = export?.Resolve(handle);
        if (node is null || export is null)
        {
            Failure(w, procedure, 70); // STALE
            return true;
        }
        var resultStart = w.Length;
        try
        {
            switch (procedure)
            {
                case 1: // GETATTR
                    w.UInt(0); export.Attributes(w, node); break;
                case 3: // LOOKUP
                    var name = r.Text(255);
                    if (!node.Directory) { Failure(w, procedure, 20); break; }
                    var child = export.Lookup(node, name);
                    if (child is null) { Failure(w, procedure, 2); break; }
                    w.UInt(0); w.Opaque(export.Handle(child));
                    export.PostAttributes(w, child); export.PostAttributes(w, node); break;
                case 4: // ACCESS
                    var access = r.UInt();
                    w.UInt(0); export.PostAttributes(w, node);
                    w.UInt(access & (node.Directory ? 35u : 1u)); break; // read / directory lookup and traversal
                case 5: // READLINK: ISO view exposes regular files/directories only
                    Failure(w, procedure, 22); break;
                case 6: // READ
                    var offset = r.ULong(); var count = r.UInt();
                    if (node.Directory) { Failure(w, procedure, 21); break; }
                    var bytes = export.Read(node, offset, count);
                    status?.RecordRead(bytes.Length);
                    w.UInt(0); export.PostAttributes(w, node); w.UInt((uint)bytes.Length);
                    w.Bool(offset >= node.Size || (ulong)bytes.Length >= node.Size - offset);
                    w.Opaque(bytes); break;
                case 16: case 17:
                    ReadDirectory(r, w, export, node, procedure == 17); break;
                case 18: // FSSTAT
                    w.UInt(0); export.PostAttributes(w, node);
                    w.ULong((ulong)export.Entry.SizeBytes); w.ULong(0); w.ULong(0);
                    w.ULong(100_000); w.ULong(0); w.ULong(0); w.UInt(0); break;
                case 19: // FSINFO
                    w.UInt(0); export.PostAttributes(w, node);
                    w.UInt(MaxRead); w.UInt(MaxRead); w.UInt(4096);
                    w.UInt(MaxRead); w.UInt(MaxRead); w.UInt(4096); w.UInt(8192);
                    w.ULong(long.MaxValue); w.UInt(1); w.UInt(0);
                    w.UInt(0x0008); break; // homogeneous; no hard links, symlinks or set-time
                case 20: // PATHCONF
                    w.UInt(0); export.PostAttributes(w, node);
                    w.UInt(1); w.UInt(255); w.Bool(true); w.Bool(true); w.Bool(false); w.Bool(true); break;
                default: return false;
            }
        }
        catch (Exception ex) when (ex is IOException && ex is not InvalidDataException && ex is not EndOfStreamException || ex is UnauthorizedAccessException)
        {
            w.Truncate(resultStart);
            Failure(w, procedure, 5); // NFS3ERR_IO, without a partially encoded success union
        }
        return true;
    }

    private static void Failure(XdrWriter w, uint procedure, uint status)
    { w.UInt(status); if (procedure != 1) w.Bool(false); }

    private static void ReadDirectory(XdrReader r, XdrWriter w, NfsExport export, NfsNode node, bool plus)
    {
        var cookie = r.ULong(); var verifier = r.Fixed(8).ToArray();
        var dirCount = r.UInt();
        var maxCount = plus ? r.UInt() : dirCount;
        var limit = (int)Math.Min(maxCount, MaxRead);
        if (!node.Directory) { Failure(w, 16, 20); return; }
        var expected = export.Generation.ToByteArray()[..8];
        var children = export.Children(node);
        if (cookie > (ulong)children.Length || cookie != 0 && !verifier.SequenceEqual(expected))
        { Failure(w, 16, 10003); return; } // BAD_COOKIE
        var entries = new XdrWriter();
        var index = (int)cookie;
        var directoryBytes = 0;
        for (; index < children.Length; index++)
        {
            var child = children[index];
            var entry = new XdrWriter();
            entry.Bool(true); entry.ULong(child.Id); entry.Text(child.Name); entry.ULong((ulong)index + 1);
            var nameBytes = entry.Length;
            if (plus) { export.PostAttributes(entry, child); entry.Bool(true); entry.Opaque(export.Handle(child)); }
            // status + post_op_attr + cookieverf + two final booleans = 108 bytes
            if (108 + entries.Length + entry.Length > limit || plus && directoryBytes + nameBytes > dirCount) break;
            entries.Fixed(entry.ToArray()); directoryBytes += nameBytes;
        }
        if (index == (int)cookie && index < children.Length || limit < 108)
        { Failure(w, 16, 10005); return; } // TOOSMALL
        w.UInt(0); export.PostAttributes(w, node); w.Fixed(expected); w.Fixed(entries.ToArray());
        w.Bool(false); w.Bool(index == children.Length);
    }

    public void Dispose()
    {
        lock (_gate) { foreach (var export in _exports.Values) export.Dispose(); _exports.Clear(); }
    }
}
