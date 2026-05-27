using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Everboot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services.Smb;

/// <summary>
/// Maps SMB share names to ISO files. One share per ISO (share name = slug),
/// each backed by a DiscUtils filesystem opened lazily when a tree connects.
/// </summary>
internal sealed class SmbShareCatalog
{
    private readonly BootCatalog _bootCatalog;
    private readonly ILogger<SmbShareCatalog> _logger;
    private readonly SmbOptions _options;

    public SmbShareCatalog(BootCatalog bootCatalog, IOptions<EverbootOptions> options, ILogger<SmbShareCatalog> logger)
    {
        _bootCatalog = bootCatalog;
        _options = options.Value.Smb;
        _logger = logger;
    }

    public SmbOptions Options => _options;

    public IReadOnlyList<string> ShareNames => _bootCatalog.Entries.Select(e => e.Slug).ToList();

    public bool TryOpen(string shareName, out SmbShare? share)
    {
        share = null;
        var entry = _bootCatalog.Entries.FirstOrDefault(e =>
            string.Equals(e.Slug, shareName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        try
        {
            // Hold the ISO file open for the lifetime of the share. Multiple
            // concurrent SMB handles share the same backing CDReader, which is
            // safe for read-only traversal.
            var stream = new FileStream(
                entry.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            DiscFileSystem fs;
            stream.Position = 0;
            if (entry.Metadata.Layout.HasFlag(IsoLayout.Iso9660))
            {
                fs = new CDReader(stream, joliet: true);
            }
            else if (entry.Metadata.Layout.HasFlag(IsoLayout.Udf))
            {
                fs = new UdfReader(stream);
            }
            else
            {
                stream.Dispose();
                _logger.LogWarning("SMB share '{Share}' refused: image has no ISO9660/UDF layout", shareName);
                return false;
            }

            share = new SmbShare(shareName, entry.DisplayName, stream, fs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open SMB share '{Share}'", shareName);
            return false;
        }
    }
}

/// <summary>
/// One open share = one ISO with its DiscUtils filesystem and the underlying
/// FileStream both kept open until disconnect.
/// </summary>
internal sealed class SmbShare : IDisposable
{
    public string Name { get; }
    public string DisplayName { get; }
    public FileStream BackingStream { get; }
    public DiscFileSystem FileSystem { get; }

    private bool _disposed;

    public SmbShare(string name, string displayName, FileStream backingStream, DiscFileSystem fs)
    {
        Name = name;
        DisplayName = displayName;
        BackingStream = backingStream;
        FileSystem = fs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (FileSystem as IDisposable)?.Dispose(); } catch { }
        try { BackingStream.Dispose(); } catch { }
    }
}
