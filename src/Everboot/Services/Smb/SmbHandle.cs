using System;
using System.Collections.Generic;
using System.IO;

namespace Everboot.Services.Smb;

/// <summary>
/// One open SMB file or directory handle, returned by CREATE and addressed
/// thereafter by its 16-byte FileId (we keep the persistent half == volatile).
/// </summary>
internal sealed class SmbHandle : IDisposable
{
    public ulong VolatileId { get; }
    public ulong PersistentId => VolatileId;
    public string Path { get; }
    public bool IsDirectory { get; }
    public long Length { get; }
    public DateTimeOffset Created { get; }
    public DateTimeOffset Modified { get; }
    public uint FileAttributes { get; }

    /// <summary>
    /// Open file stream if this handle is on a file; null for directories.
    /// </summary>
    public Stream? FileStream { get; }

    /// <summary>
    /// Cached directory enumeration so QUERY_DIRECTORY can paginate.
    /// </summary>
    public IList<SmbDirectoryEntry>? Listing { get; set; }
    public int ListingPosition { get; set; }
    public string? LastListingPattern { get; set; }

    private bool _disposed;

    public SmbHandle(
        ulong id,
        string path,
        bool isDirectory,
        long length,
        DateTimeOffset created,
        DateTimeOffset modified,
        uint fileAttributes,
        Stream? fileStream)
    {
        VolatileId = id;
        Path = path;
        IsDirectory = isDirectory;
        Length = length;
        Created = created;
        Modified = modified;
        FileAttributes = fileAttributes;
        FileStream = fileStream;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { FileStream?.Dispose(); } catch { }
    }
}

/// <summary>
/// One entry in a directory listing - just enough to build the various
/// FileXxxDirectoryInformation responses without revisiting the filesystem.
/// </summary>
internal sealed record SmbDirectoryEntry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    uint FileAttributes);
