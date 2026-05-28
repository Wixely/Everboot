using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Everboot.Configuration;
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
internal sealed class HttpFileService : BackgroundService
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly ILogger<HttpFileService> _logger;
    private readonly BootCatalog _catalog;
    private readonly BootConfigGenerator _generator;
    private readonly EverbootOptions _options;

    public HttpFileService(
        ILogger<HttpFileService> logger,
        BootCatalog catalog,
        BootConfigGenerator generator,
        IOptions<EverbootOptions> options)
    {
        _logger = logger;
        _catalog = catalog;
        _generator = generator;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        try
        {
            if (request.HttpMethod == "POST" && path == "/refresh")
            {
                await HandleRefreshAsync(response, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.AddHeader("Allow", "GET, HEAD, POST");
                return;
            }

            if (path is "/" or "/index.html")
            {
                await WriteIndexAsync(response, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (path is "/diagnose" or "/diagnose.html")
            {
                await WriteDiagnoseAsync(response, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (path is "/boot.ipxe" or "/boot.cfg")
            {
                await WriteIpxeMenuAsync(request, response, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/isos/", StringComparison.Ordinal))
            {
                var rawName = path["/isos/".Length..];
                await WriteIsoAsync(request, response, rawName, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/iso/", StringComparison.Ordinal))
            {
                var rest = path["/iso/".Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1)
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
                var slug = rest[..slash];
                var innerPath = Uri.UnescapeDataString(rest[(slash + 1)..]);
                await WriteIsoContentAsync(request, response, slug, innerPath, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/files/", StringComparison.Ordinal))
            {
                var rawName = path["/files/".Length..];
                await WriteSupportFileAsync(request, response, rawName, stoppingToken).ConfigureAwait(false);
                return;
            }

            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WritePlainTextAsync(request, response, "not found\n", stoppingToken).ConfigureAwait(false);
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

    private async Task WriteIpxeMenuAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var authority = ResolveAuthority(request);
        var script = _generator.GenerateIpxeMenu(authority);
        var bytes = Encoding.UTF8.GetBytes(script);

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        if (request.HttpMethod != "HEAD")
        {
            await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Served iPXE menu to {Remote} ({Count} entries)", request.RemoteEndPoint, _catalog.Entries.Count);
    }

    private async Task WriteIndexAsync(HttpListenerResponse response, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Everboot</title>");
        sb.AppendLine("<style>body{font-family:system-ui,sans-serif;max-width:48rem;margin:2rem auto;padding:0 1rem;color:#111}");
        sb.AppendLine("table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:.4rem .6rem;border-bottom:1px solid #ddd}");
        sb.AppendLine("code{background:#f4f4f4;padding:.1rem .3rem;border-radius:3px}");
        sb.AppendLine("button{font:inherit;padding:.4rem .8rem;border:1px solid #999;background:#f4f4f4;border-radius:4px;cursor:pointer}");
        sb.AppendLine("button:hover{background:#e8e8e8}.toolbar{margin:1rem 0;display:flex;gap:.5rem;align-items:center}</style></head><body>");
        sb.AppendLine("<h1>Everboot</h1>");
        sb.AppendLine($"<p>iPXE menu: <code><a href=\"/boot.ipxe\">/boot.ipxe</a></code>. ISO directory: <code>{WebUtility.HtmlEncode(_catalog.IsoDirectory)}</code>.</p>");

        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("  <form method=\"post\" action=\"/refresh\" style=\"display:inline;margin:0\">");
        sb.AppendLine("    <button type=\"submit\">Refresh catalog</button>");
        sb.AppendLine("  </form>");
        sb.AppendLine("  <a href=\"/diagnose\">Diagnose detection</a>");
        sb.AppendLine("</div>");

        var entries = _catalog.Entries;
        if (entries.Count == 0)
        {
            sb.AppendLine("<p><em>No images found. Drop <code>*.iso</code> or <code>*.img</code> files into the directory above, then click <strong>Refresh catalog</strong>.</em></p>");
        }
        else
        {
            sb.AppendLine("<table><thead><tr><th>Name</th><th>Size</th><th>Download</th></tr></thead><tbody>");
            foreach (var entry in entries)
            {
                var url = $"/isos/{Uri.EscapeDataString(entry.FileName)}";
                sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(entry.DisplayName)).Append("</td>")
                  .Append("<td>").Append(FormatSize(entry.SizeBytes)).Append("</td>")
                  .Append("<td><a href=\"").Append(url).Append("\">").Append(WebUtility.HtmlEncode(entry.FileName)).AppendLine("</a></td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        sb.AppendLine("</body></html>");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task HandleRefreshAsync(HttpListenerResponse response, CancellationToken ct)
    {
        _logger.LogInformation("Refresh catalog requested via web UI");
        _catalog.ForceRescan();
        response.StatusCode = (int)HttpStatusCode.SeeOther;
        response.AddHeader("Location", "/");
        response.ContentLength64 = 0;
        await Task.CompletedTask;
    }

    private async Task WriteDiagnoseAsync(HttpListenerResponse response, CancellationToken ct)
    {
        var entries = _catalog.Entries;
        var sb = new StringBuilder(8192);

        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Everboot - diagnose</title>");
        sb.AppendLine("<style>body{font-family:system-ui,sans-serif;max-width:72rem;margin:2rem auto;padding:0 1rem;color:#111}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:.9rem}th,td{text-align:left;padding:.4rem .6rem;border-bottom:1px solid #ddd;vertical-align:top}");
        sb.AppendLine("th{background:#f4f4f4}code{background:#f4f4f4;padding:.1rem .3rem;border-radius:3px;font-size:.85em}");
        sb.AppendLine(".no{color:#999}.yes{color:#080}.warn{color:#a60}</style></head><body>");
        sb.AppendLine("<h1>Catalog detection diagnostics</h1>");
        sb.AppendLine($"<p><a href=\"/\">&larr; back to index</a></p>");
        sb.AppendLine($"<p>ISO directory: <code>{WebUtility.HtmlEncode(_catalog.IsoDirectory)}</code> &nbsp;|&nbsp; {entries.Count} image(s)</p>");

        if (entries.Count == 0)
        {
            sb.AppendLine("<p><em>No images in catalog.</em></p>");
        }
        else
        {
            sb.AppendLine("<table><thead><tr>");
            sb.AppendLine("<th>File</th><th>Size</th><th>Volume label</th><th>Layout</th><th>Windows era</th><th>Profile</th><th>Direct boot</th><th>Floppy</th>");
            sb.AppendLine("</tr></thead><tbody>");

            foreach (var entry in entries)
            {
                var m = entry.Metadata;
                var profileCell = m.Profile is { } p
                    ? $"<span class=\"yes\">{WebUtility.HtmlEncode(p.Id)}</span><br><small>{WebUtility.HtmlEncode(p.DisplayName)} &middot; {p.Family}</small>"
                    : "<span class=\"no\">(no match)</span>";

                var layoutCell = m.Layout == IsoLayout.None
                    ? "<span class=\"no\">none (raw image)</span>"
                    : WebUtility.HtmlEncode(m.Layout.ToString());

                var eraCell = m.WindowsEra == WindowsBootEra.None
                    ? "<span class=\"no\">none</span>"
                    : (m.WindowsEra == WindowsBootEra.Modern ? "<span class=\"yes\">Modern</span>" : $"<span class=\"warn\">{m.WindowsEra}</span>");

                var directBootCell = m.Profile?.DirectBoot is { } db
                    ? $"<span class=\"yes\">yes</span><br><small><code>{WebUtility.HtmlEncode(db.KernelPath)}</code></small>"
                    : "<span class=\"no\">no</span>";

                var floppyCell = entry.IsLikelyFloppy ? "<span class=\"yes\">yes</span>" : "<span class=\"no\">no</span>";

                sb.Append("<tr>")
                  .Append("<td>").Append(WebUtility.HtmlEncode(entry.FileName)).Append("<br><small>slug: <code>").Append(WebUtility.HtmlEncode(entry.Slug)).Append("</code></small></td>")
                  .Append("<td>").Append(FormatSize(entry.SizeBytes)).Append("</td>")
                  .Append("<td>").Append(m.VolumeLabel is null ? "<span class=\"no\">none</span>" : $"<code>{WebUtility.HtmlEncode(m.VolumeLabel)}</code>").Append("</td>")
                  .Append("<td>").Append(layoutCell).Append("</td>")
                  .Append("<td>").Append(eraCell).Append("</td>")
                  .Append("<td>").Append(profileCell).Append("</td>")
                  .Append("<td>").Append(directBootCell).Append("</td>")
                  .Append("<td>").Append(floppyCell).Append("</td>")
                  .AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");

            sb.AppendLine("<h2>Reading the table</h2><ul>");
            sb.AppendLine("<li><strong>Profile = (no match)</strong> means none of the codified distro patterns recognised this image. It falls through to plain sanboot. To make detection match, the volume label needs to start with the expected prefix (e.g. <code>Ubuntu</code>, <code>Pop_OS</code>) <em>and</em> the expected marker files must exist at the expected paths inside the ISO.</li>");
            sb.AppendLine("<li><strong>Direct boot = yes</strong> means the iPXE menu emits a direct kernel+initrd entry for this image. <strong>no</strong> means it sanboots.</li>");
            sb.AppendLine("<li><strong>Layout = none (raw image)</strong> means we couldn't read an ISO9660 or UDF filesystem inside - either a raw disk image (typical for <code>.img</code>) or a corrupt ISO.</li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</body></html>");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task WriteIsoAsync(HttpListenerRequest request, HttpListenerResponse response, string rawName, CancellationToken ct)
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
        var partial = TryParseRange(request.Headers["Range"], total, ref start, ref end);

        if (partial && (start > end || start < 0 || start >= total))
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{total}");
            return;
        }

        var length = end - start + 1;
        response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = length;
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Content-Disposition", $"attachment; filename=\"{entry.FileName}\"");
        if (partial)
        {
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
        }

        if (request.HttpMethod == "HEAD")
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

    private async Task WriteIsoContentAsync(HttpListenerRequest request, HttpListenerResponse response, string slug, string innerPath, CancellationToken ct)
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
            var partial = TryParseRange(request.Headers["Range"], total, ref start, ref end);

            if (partial && (start > end || start < 0 || start >= total))
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.AddHeader("Content-Range", $"bytes */{total}");
                return;
            }

            var length = end - start + 1;
            response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = length;
            response.AddHeader("Accept-Ranges", "bytes");
            if (partial)
            {
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
            }

            if (request.HttpMethod == "HEAD")
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

    private async Task WriteSupportFileAsync(HttpListenerRequest request, HttpListenerResponse response, string rawName, CancellationToken ct)
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
        var partial = TryParseRange(request.Headers["Range"], total, ref start, ref end);

        if (partial && (start > end || start < 0 || start >= total))
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{total}");
            return;
        }

        var length = end - start + 1;
        response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = length;
        response.AddHeader("Accept-Ranges", "bytes");
        if (partial)
        {
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
        }

        if (request.HttpMethod == "HEAD")
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

    private static DiscFileSystem? OpenFileSystem(Stream isoStream, IsoLayout layout)
    {
        isoStream.Position = 0;
        if (layout.HasFlag(IsoLayout.Iso9660))
        {
            return new CDReader(isoStream, joliet: true);
        }
        if (layout.HasFlag(IsoLayout.Udf))
        {
            return new UdfReader(isoStream);
        }
        return null;
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

    private static string ResolveAuthority(HttpListenerRequest request)
    {
        var host = request.Headers["Host"];
        if (!string.IsNullOrEmpty(host))
        {
            return host;
        }
        var localEp = request.LocalEndPoint;
        return localEp is null ? "everboot" : $"{localEp.Address}:{localEp.Port}";
    }

    private static async Task WritePlainTextAsync(HttpListenerRequest request, HttpListenerResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        if (request.HttpMethod == "HEAD")
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
