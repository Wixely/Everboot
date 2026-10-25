using System.Collections.Specialized;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Everboot.Services;

/// <summary>
/// Narrow transport boundary for the compatibility-sensitive PXE endpoints.
/// It lets the same range and streaming implementation run behind HttpListener
/// and Kestrel while the migration is verified.
/// </summary>
internal sealed class BootHttpRequest
{
    private readonly Func<string, string?> _header;

    private BootHttpRequest(string method, string path, NameValueCollection query,
        Func<string, string?> header, IPEndPoint? remote, IPEndPoint? local)
    {
        Method = method;
        Path = path;
        Query = query;
        _header = header;
        RemoteEndPoint = remote;
        LocalEndPoint = local;
    }

    public string Method { get; }
    public string Path { get; }
    public NameValueCollection Query { get; }
    public IPEndPoint? RemoteEndPoint { get; }
    public IPEndPoint? LocalEndPoint { get; }
    public string? Header(string name) => _header(name);

    public static BootHttpRequest From(HttpListenerRequest request) => new(
        request.HttpMethod,
        request.Url?.AbsolutePath ?? "/",
        request.QueryString,
        name => request.Headers[name],
        request.RemoteEndPoint,
        request.LocalEndPoint);

    public static BootHttpRequest From(HttpRequest request)
    {
        var query = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Query)
            foreach (var value in pair.Value)
                query.Add(pair.Key, value);
        var connection = request.HttpContext.Connection;
        var remote = connection.RemoteIpAddress is null ? null : new IPEndPoint(connection.RemoteIpAddress, connection.RemotePort);
        var local = connection.LocalIpAddress is null ? null : new IPEndPoint(connection.LocalIpAddress, connection.LocalPort);
        return new(request.Method, request.Path.Value ?? "/", query,
            name => request.Headers.TryGetValue(name, out var value) ? value.ToString() : null,
            remote, local);
    }
}

internal sealed class BootHttpResponse
{
    private readonly Action<int> _status;
    private readonly Action<string?> _contentType;
    private readonly Action<long> _contentLength;
    private readonly Action<string, string> _header;

    private BootHttpResponse(Stream outputStream, Action<int> status, Action<string?> contentType,
        Action<long> contentLength, Action<string, string> header)
    {
        OutputStream = outputStream;
        _status = status;
        _contentType = contentType;
        _contentLength = contentLength;
        _header = header;
    }

    public Stream OutputStream { get; }
    public int StatusCode { set => _status(value); }
    public string? ContentType { set => _contentType(value); }
    public long ContentLength { set => _contentLength(value); }
    public void AddHeader(string name, string value) => _header(name, value);

    public static BootHttpResponse From(HttpListenerResponse response) => new(
        response.OutputStream,
        value => response.StatusCode = value,
        value => response.ContentType = value,
        value => response.ContentLength64 = value,
        (name, value) => response.AddHeader(name, value));

    public static BootHttpResponse From(HttpResponse response) => new(
        response.Body,
        value => response.StatusCode = value,
        value => response.ContentType = value,
        value => response.ContentLength = value,
        (name, value) => response.Headers[name] = value);
}
