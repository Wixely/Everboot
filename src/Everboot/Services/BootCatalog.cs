using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services;

internal sealed record IsoEntry(
    string FileName,
    string Slug,
    string DisplayName,
    long SizeBytes,
    string FullPath,
    IsoMetadata Metadata,
    bool IsLikelyFloppy);

/// <summary>
/// Scans the configured ISO directory and keeps a live list of bootable images.
/// Watches the directory and rescans on change with a small debounce so copies
/// in progress don't generate half-baked entries.
/// </summary>
internal sealed class BootCatalog : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<BootCatalog> _logger;
    private readonly IsoInspector _inspector;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher _watcher;
    private IReadOnlyList<IsoEntry> _entries = Array.Empty<IsoEntry>();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public string DataDirectory { get; }
    public string IsoDirectory { get; }
    public string TftpDirectory { get; }

    public BootCatalog(IOptions<EverbootOptions> options, IsoInspector inspector, ILogger<BootCatalog> logger)
    {
        _logger = logger;
        _inspector = inspector;
        DataDirectory = Path.GetFullPath(options.Value.DataDirectory);
        IsoDirectory = Path.Combine(DataDirectory, "isos");
        TftpDirectory = Path.Combine(DataDirectory, "tftp");
        Directory.CreateDirectory(IsoDirectory);
        Directory.CreateDirectory(TftpDirectory);

        Rescan();

        _watcher = new FileSystemWatcher(IsoDirectory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, _) => DebouncedRescan();
        _watcher.Deleted += (_, _) => DebouncedRescan();
        _watcher.Renamed += (_, _) => DebouncedRescan();
        _watcher.Changed += (_, _) => DebouncedRescan();
        _watcher.Error += (_, e) => _logger.LogError(e.GetException(), "ISO watcher error");
    }

    public IReadOnlyList<IsoEntry> Entries
    {
        get { lock (_gate) { return _entries; } }
    }

    public bool TryGetByFileName(string fileName, out IsoEntry? entry)
    {
        entry = Entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    private void DebouncedRescan()
    {
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _debounceCts, cts);
        prev?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, cts.Token).ConfigureAwait(false);
                Rescan();
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ISO rescan failed");
            }
        });
    }

    private static readonly string[] ImageExtensions = { ".iso", ".img" };

    /// <summary>
    /// Largest size we treat as a floppy candidate (max 2.88 MB physical + slack).
    /// Anything bigger gets the regular sanboot treatment.
    /// </summary>
    private const long FloppySizeCeiling = 4L * 1024 * 1024;

    private void Rescan()
    {
        var entries = new List<IsoEntry>();
        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(IsoDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));

        foreach (var path in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping {Path} (stat failed)", path);
                continue;
            }

            var fileName = info.Name;
            var slug = MakeUniqueSlug(fileName, usedSlugs);
            var display = Path.GetFileNameWithoutExtension(fileName);
            var metadata = _inspector.Inspect(info.FullName);
            var isFloppy =
                string.Equals(Path.GetExtension(fileName), ".img", StringComparison.OrdinalIgnoreCase) &&
                info.Length <= FloppySizeCeiling &&
                metadata.Layout == IsoLayout.None;
            entries.Add(new IsoEntry(fileName, slug, display, info.Length, info.FullName, metadata, isFloppy));
        }

        entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        lock (_gate)
        {
            _entries = entries;
        }

        var modernCount = 0;
        var xpCount = 0;
        var ninexCount = 0;
        var floppyCount = 0;
        foreach (var e in entries)
        {
            switch (e.Metadata.WindowsEra)
            {
                case WindowsBootEra.Modern: modernCount++; break;
                case WindowsBootEra.XpClass: xpCount++; break;
                case WindowsBootEra.NinexClass: ninexCount++; break;
            }
            if (e.IsLikelyFloppy)
            {
                floppyCount++;
            }
        }

        _logger.LogInformation(
            "Catalog refreshed: {Count} image(s) - modern Windows={Modern}, XP-class={Xp}, 9x-class={Ninex}, floppy={Floppy}; root={Directory}",
            entries.Count, modernCount, xpCount, ninexCount, floppyCount, IsoDirectory);

        if (modernCount > 0 && !File.Exists(Path.Combine(TftpDirectory, "wimboot")))
        {
            _logger.LogWarning(
                "Found {Count} Windows ISO(s) but data/tftp/wimboot is missing - Windows entries will fail to boot. " +
                "Download wimboot from https://ipxe.org/wimboot and place it at {Path}",
                modernCount, Path.Combine(TftpDirectory, "wimboot"));
        }
        if (floppyCount > 0 && !File.Exists(Path.Combine(TftpDirectory, "memdisk")))
        {
            _logger.LogWarning(
                "Found {Count} floppy-class image(s) but data/tftp/memdisk is missing - they will fail to boot. " +
                "Get memdisk from the syslinux project and drop it at {Path}",
                floppyCount, Path.Combine(TftpDirectory, "memdisk"));
        }

        foreach (var entry in entries)
        {
            if (entry.Metadata.Profile is { } profile && profile.Caveat is not null)
            {
                _logger.LogWarning(
                    "Image {File} matched profile '{Profile}': {Caveat}",
                    entry.FileName, profile.DisplayName, profile.Caveat);
            }
        }
    }

    public bool TryGetBySlug(string slug, out IsoEntry? entry)
    {
        entry = Entries.FirstOrDefault(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    private static string MakeUniqueSlug(string fileName, HashSet<string> used)
    {
        var baseSlug = SlugFor(fileName);
        var slug = baseSlug;
        var suffix = 2;
        while (!used.Add(slug))
        {
            slug = $"{baseSlug}-{suffix++}";
        }
        return slug;
    }

    private static string SlugFor(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('-');
            }
        }
        var slug = sb.ToString().Trim('-', '.');
        return string.IsNullOrEmpty(slug) ? "iso" : slug;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _debounceCts?.Cancel();
        _watcher.Dispose();
    }
}
