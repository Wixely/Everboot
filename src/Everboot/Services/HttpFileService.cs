using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils;
using Everboot.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services;

/// <summary>
/// HTTP file server. Serves:
///   GET /                  human-readable index of available ISOs
///   GET /boot.ipxe         dynamic iPXE menu (clients chainload this)
///   GET /isos/{file.iso}   the ISO bytes, with Range support for sanboot
/// </summary>
internal sealed partial class HttpFileService : BackgroundService
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly ILogger<HttpFileService> _logger;
    private readonly BootCatalog _catalog;
    private readonly BootConfigGenerator _generator;
    private readonly EverbootOptions _options;
    private readonly bool _useLegacyListener;
    private long _dosBytesRead;
    public long DosBytesRead => Interlocked.Read(ref _dosBytesRead);
    private readonly Lock _dosGate = new();
    private readonly Dictionary<string, (long Size, DateTime Modified, DosSourceDisk Disk)> _dosDisks = [];

    public HttpFileService(
        ILogger<HttpFileService> logger,
        BootCatalog catalog,
        BootConfigGenerator generator,
        IOptions<EverbootOptions> options) : this(logger, catalog, generator, options, false) { }

    internal HttpFileService(
        ILogger<HttpFileService> logger,
        BootCatalog catalog,
        BootConfigGenerator generator,
        IOptions<EverbootOptions> options,
        bool useLegacyListener)
    {
        _logger = logger;
        _catalog = catalog;
        _generator = generator;
        _options = options.Value;
        _useLegacyListener = useLegacyListener;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_useLegacyListener)
        {
            _logger.LogInformation("PXE HTTP endpoints are hosted by Kestrel");
            return;
        }
        if (!HttpListener.IsSupported)
        {
            _logger.LogCritical("HttpListener is not supported on this platform");
            return;
        }

        using var listener = new HttpListener();
        var prefix = $"http://{_options.Http.BindAddress}:{_options.Http.Port}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogCritical(ex,
                "Failed to bind HTTP on {Prefix}. On Windows non-admin runs need: " +
                "netsh http add urlacl url={Prefix} user=Everyone",
                prefix, prefix);
            throw;
        }

        _logger.LogInformation("HTTP file server listening on {Prefix} (iso dir: {Dir})", prefix, _catalog.IsoDirectory);

        await using var registration = stoppingToken.Register(() =>
        {
            try { listener.Stop(); } catch { /* ignore */ }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, stoppingToken), stoppingToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";
        var bootRequest = BootHttpRequest.From(request);
        var bootResponse = BootHttpResponse.From(response);

        try
        {
            await HandleBootTransportAsync(bootRequest, bootResponse, stoppingToken).ConfigureAwait(false);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogDebug(ex, "Client disconnected during {Method} {Path}", request.HttpMethod, path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Method} {Path}", request.HttpMethod, path);
            try { response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    internal async Task HandleKestrelAsync(HttpContext context)
    {
        var request = BootHttpRequest.From(context.Request);
        var response = BootHttpResponse.From(context.Response);
        try
        {
            await HandleBootTransportAsync(request, response, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (IOException ex) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Client disconnected during {Method} {Path}", request.Method, request.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Method} {Path}", request.Method, request.Path);
            if (!context.Response.HasStarted) context.Response.StatusCode = 500;
        }
    }

    private async Task HandleBootTransportAsync(BootHttpRequest request, BootHttpResponse response, CancellationToken ct)
    {
        var path = request.Path;
        if (request.Method is not ("GET" or "HEAD"))
        {
            response.StatusCode = 405;
            response.AddHeader("Allow", "GET, HEAD");
            return;
        }
        if (path == "/catalog.json") { await WriteCatalogAsync(request, response, ct); return; }
        if (path is "/boot.ipxe" or "/boot.cfg") { await WriteIpxeMenuAsync(request, response, ct); return; }
        if (path.StartsWith("/winpe/", StringComparison.Ordinal))
        {
            var parts = path.Split('/');
            if (parts.Length != 4 || !_options.Smb.Enabled || _options.Smb.Port != 445 ||
                !_catalog.TryGetBySlug(parts[2], out var entry) || entry?.Metadata.WindowsFiles is null ||
                parts[3] is not ("winpeshl.ini" or "everboot.cmd"))
            { response.StatusCode = 404; return; }
            string script;
            try { script = parts[3] == "winpeshl.ini" ? WinPeStartup.Shell : WinPeStartup.Script(ResolveAuthority(request), entry.Slug); }
            catch (ArgumentException) { response.StatusCode = 400; return; }
            response.AddHeader("Cache-Control", "no-store");
            await WritePlainTextAsync(request, response, script, ct); return;
        }
        if (path.StartsWith("/isos/", StringComparison.Ordinal)) { await WriteIsoAsync(request, response, path["/isos/".Length..], ct); return; }
        if (path.StartsWith("/iso/", StringComparison.Ordinal))
        {
            var rest = path["/iso/".Length..]; var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) { response.StatusCode = 404; return; }
            await WriteIsoContentAsync(request, response, rest[..slash], Uri.UnescapeDataString(rest[(slash + 1)..]), ct); return;
        }
        if (path.StartsWith("/dos-source/", StringComparison.Ordinal)) { await WriteDosSourceAsync(request, response, path["/dos-source/".Length..], ct); return; }
        if (path.StartsWith("/files/", StringComparison.Ordinal)) { await WriteSupportFileAsync(request, response, path["/files/".Length..], ct); return; }
        response.StatusCode = 404;
        await WritePlainTextAsync(request, response, "not found\n", ct);
    }

    private async Task WriteCatalogAsync(BootHttpRequest request, BootHttpResponse response, CancellationToken ct)
    {
        var images = _catalog.Entries.Select(e => Vm.VmRecipes.Describe(e, _catalog.TftpDirectory));
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(images);
        response.ContentType = "application/json; charset=utf-8";
        response.AddHeader("Cache-Control", "no-store");
        response.ContentLength = bytes.Length;
        if (request.Method != "HEAD") await response.OutputStream.WriteAsync(bytes, ct);
    }

    private async Task WriteIpxeMenuAsync(BootHttpRequest request, BootHttpResponse response, CancellationToken ct)
    {
        var authority = ResolveAuthority(request);
        response.AddHeader("Cache-Control", "no-store");
        string script;
        if (request.Query["identity"] != "1")
            script = $"#!ipxe\nchain http://{authority}/boot.ipxe?identity=1&mac=${{netX/mac}}&ip=${{netX/ip}} || exit\n";
        else
        {
            try
            {
                var decision = _catalog.Policies.Decide(_catalog.Entries, request.Query["mac"],
                    string.IsNullOrWhiteSpace(request.Query["ip"]) ? request.RemoteEndPoint?.Address.ToString() : request.Query["ip"]);
                script = _generator.GenerateIpxeMenu(authority, decision);
            }
            catch (ArgumentException) { response.StatusCode = 400; return; }
        }
        var bytes = Encoding.UTF8.GetBytes(script);

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength = bytes.Length;
        if (request.Method != "HEAD")
        {
            await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Served iPXE menu to {Remote} ({Count} entries)", request.RemoteEndPoint, _catalog.Entries.Count);
    }

    private async Task WriteIsoAsync(BootHttpRequest request, BootHttpResponse response, string rawName, CancellationToken ct)
    {
        string fileName;
        try
        {
            fileName = Uri.UnescapeDataString(rawName);
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (string.IsNullOrEmpty(fileName) ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(fileName))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (!_catalog.TryGetByFileName(fileName, out var entry) || entry is null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WritePlainTextAsync(request, response, $"iso '{fileName}' not in catalog\n", ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(entry.FullPath))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var info = new FileInfo(entry.FullPath);
        var total = info.Length;
        long start = 0;
        long end = total - 1;
        var partial = TryParseRange(request.Header("Range"), total, ref start, ref end);

        if (partial && (start > end || start < 0 || start >= total))
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{total}");
            return;
        }

        var length = end - start + 1;
        response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength = length;
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Content-Disposition", $"attachment; filename=\"{entry.FileName}\"");
        if (partial)
        {
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
        }

        if (request.Method == "HEAD")
        {
            return;
        }

        await using var file = new FileStream(
            entry.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        file.Seek(start, SeekOrigin.Begin);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await file.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _logger.LogInformation("Served {File} bytes {Start}-{End}/{Total} to {Remote}",
            entry.FileName, start, end, total, request.RemoteEndPoint);
    }

    private async Task WriteIsoContentAsync(BootHttpRequest request, BootHttpResponse response, string slug, string innerPath, CancellationToken ct)
    {
        if (!_catalog.TryGetBySlug(slug, out var entry) || entry is null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WritePlainTextAsync(request, response, $"iso '{slug}' not in catalog\n", ct).ConfigureAwait(false);
            return;
        }

        innerPath = innerPath.TrimStart('/');
        if (string.IsNullOrEmpty(innerPath) ||
            innerPath.Contains("..", StringComparison.Ordinal) ||
            innerPath.Contains('\\') ||
            Path.IsPathRooted(innerPath))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        var isoStream = new FileStream(
            entry.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        DiscFileSystem? fs = null;
        Stream? inner = null;
        try
        {
            fs = OpenFileSystem(isoStream, entry.Metadata.Layout);
            if (fs is null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            var resolved = IsoFileResolver.Resolve(fs, innerPath);
            if (resolved is null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WritePlainTextAsync(request, response, $"'{innerPath}' not found inside iso\n", ct).ConfigureAwait(false);
                return;
            }

            inner = fs.OpenFile(resolved, FileMode.Open, FileAccess.Read);
            var total = inner.Length;
            long start = 0;
            long end = total - 1;
            var partial = TryParseRange(request.Header("Range"), total, ref start, ref end);

            if (partial && (start > end || start < 0 || start >= total))
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.AddHeader("Content-Range", $"bytes */{total}");
                return;
            }

            var length = end - start + 1;
            response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
            response.ContentType = "application/octet-stream";
            response.ContentLength = length;
            response.AddHeader("Accept-Ranges", "bytes");
            if (partial)
            {
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
            }

            if (request.Method == "HEAD")
            {
                return;
            }

            inner.Seek(start, SeekOrigin.Begin);
            await CopyExactAsync(inner, response.OutputStream, length, ct).ConfigureAwait(false);

            _logger.LogInformation("Served iso://{Slug}/{Path} bytes {Start}-{End}/{Total} to {Remote}",
                entry.Slug, innerPath, start, end, total, request.RemoteEndPoint);
        }
        finally
        {
            inner?.Dispose();
            (fs as IDisposable)?.Dispose();
            await isoStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteSupportFileAsync(BootHttpRequest request, BootHttpResponse response, string rawName, CancellationToken ct)
    {
        string name;
        try
        {
            name = Uri.UnescapeDataString(rawName);
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (string.IsNullOrEmpty(name) ||
            name.Contains('/') || name.Contains('\\') ||
            name.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        var fullPath = Path.GetFullPath(Path.Combine(_catalog.TftpDirectory, name));
        if (!fullPath.StartsWith(_catalog.TftpDirectory, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var info = new FileInfo(fullPath);
        var total = info.Length;
        long start = 0;
        long end = total - 1;
        var partial = TryParseRange(request.Header("Range"), total, ref start, ref end);

        if (partial && (start > end || start < 0 || start >= total))
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{total}");
            return;
        }

        var length = end - start + 1;
        response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength = length;
        response.AddHeader("Accept-Ranges", "bytes");
        if (partial)
        {
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
        }

        if (request.Method == "HEAD")
        {
            return;
        }

        await using var file = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        file.Seek(start, SeekOrigin.Begin);
        await CopyExactAsync(file, response.OutputStream, length, ct).ConfigureAwait(false);

        _logger.LogInformation("Served file://{Name} bytes {Start}-{End}/{Total} to {Remote}",
            name, start, end, total, request.RemoteEndPoint);
    }

    private async Task WriteDosSourceAsync(BootHttpRequest request, BootHttpResponse response, string slug, CancellationToken ct)
    {
        var entry = _catalog.Entries.FirstOrDefault(e => e.Slug == slug && e.Metadata.WindowsEra == WindowsBootEra.NinexClass);
        if (entry is null || !File.Exists(Path.Combine(_catalog.TftpDirectory, entry.FileName + ".dos.img")))
        { response.StatusCode = 404; return; }
        using var iso = File.OpenRead(entry.FullPath);
        using var fs = IsoFileSystem.Open(iso, entry.Metadata.Layout) ?? throw new IOException("ISO filesystem missing.");
        DosSourceDisk disk;
        var modified = File.GetLastWriteTimeUtc(entry.FullPath);
        lock (_dosGate)
        {
            if (!_dosDisks.TryGetValue(entry.FullPath, out var cached) || cached.Size != iso.Length || cached.Modified != modified)
            {
                // Bound cached metadata even when the catalog changes repeatedly.
                if (_dosDisks.Count >= 16) _dosDisks.Clear();
                cached = (iso.Length, modified, new DosSourceDisk(fs));
                _dosDisks[entry.FullPath] = cached;
            }
            disk = cached.Disk;
        }
        long start = 0, end = DosSourceDisk.Length - 1;
        var partial = TryParseRange(request.Header("Range"), DosSourceDisk.Length, ref start, ref end);
        if (partial && (start > end || start < 0 || start >= DosSourceDisk.Length))
        { response.StatusCode = 416; response.AddHeader("Content-Range", $"bytes */{DosSourceDisk.Length}"); return; }
        response.StatusCode = partial ? 206 : 200;
        response.ContentType = "application/octet-stream";
        response.ContentLength = end - start + 1;
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Cache-Control", "no-store");
        if (partial) response.AddHeader("Content-Range", $"bytes {start}-{end}/{DosSourceDisk.Length}");
        if (request.Method == "HEAD") return;
        var buffer = new byte[CopyBufferSize];
        while (start <= end)
        {
            var count = (int)Math.Min(buffer.Length, end - start + 1);
            disk.Read(fs, start, buffer.AsSpan(0, count));
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, count), ct);
            Interlocked.Add(ref _dosBytesRead, count);
            start += count;
        }
    }

    private static DiscFileSystem? OpenFileSystem(Stream isoStream, IsoLayout layout)
    {
        return IsoFileSystem.Open(isoStream, layout);
    }

    private static async Task CopyExactAsync(Stream source, Stream destination, long length, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseRange(string? header, long total, ref long start, ref long end)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header["bytes=".Length..].Trim();
        var comma = spec.IndexOf(',');
        if (comma >= 0)
        {
            spec = spec[..comma]; // we don't support multi-range; just honour the first.
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startPart = spec[..dash];
        var endPart = spec[(dash + 1)..];

        if (startPart.Length == 0)
        {
            // suffix range: bytes=-N (last N bytes)
            if (!long.TryParse(endPart, out var suffix) || suffix <= 0)
            {
                return false;
            }
            start = Math.Max(0, total - suffix);
            end = total - 1;
            return true;
        }

        if (!long.TryParse(startPart, out start))
        {
            return false;
        }

        if (endPart.Length == 0)
        {
            end = total - 1;
        }
        else if (long.TryParse(endPart, out var parsedEnd))
        {
            end = Math.Min(parsedEnd, total - 1);
        }
        else
        {
            return false;
        }

        return true;
    }

    private static string ResolveAuthority(BootHttpRequest request)
    {
        var host = request.Header("Host");
        if (!string.IsNullOrEmpty(host))
        {
            return host;
        }
        var localEp = request.LocalEndPoint;
        return localEp is null ? "everboot" : $"{localEp.Address}:{localEp.Port}";
    }

    private static async Task WritePlainTextAsync(BootHttpRequest request, BootHttpResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength = bytes.Length;
        if (request.Method == "HEAD")
        {
            return;
        }
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static string FormatSize(long bytes)
    {
        const double Kb = 1024d;
        const double Mb = Kb * 1024d;
        const double Gb = Mb * 1024d;

        return bytes switch
        {
            < (long)Kb => $"{bytes} B",
            < (long)Mb => $"{bytes / Kb:F0} KB",
            < (long)Gb => $"{bytes / Mb:F0} MB",
            _ => $"{bytes / Gb:F2} GB",
        };
    }
}
