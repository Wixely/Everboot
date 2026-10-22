using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Everboot.Services.Vm;

/// <summary>Local read-only NBD adapter. Oldstyle negotiation avoids exporting any
/// control surface; a connection has exactly one fixed image. HTTP stays in .NET,
/// avoiding Windows QEMU curl/AIO incompatibilities. Memory is bounded per read.</summary>
internal sealed class HttpBlockBridge : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Uri _media;
    private readonly long _size;
    public int Port { get; }
    public HttpBlockBridge(Uri media, long size)
    {
        _media = media; _size = size;
        _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public string Block => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["driver"] = "raw",
        ["node-name"] = "media",
        ["read-only"] = true,
        ["file"] = new { driver = "nbd", server = new { type = "inet", host = "127.0.0.1", port = Port.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
    });

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var hello = new byte[152];
                BinaryPrimitives.WriteUInt64BigEndian(hello, 0x4e42444d41474943); // NBDMAGIC
                BinaryPrimitives.WriteUInt64BigEndian(hello.AsSpan(8), 0x0000420281861253); // oldstyle
                BinaryPrimitives.WriteUInt64BigEndian(hello.AsSpan(16), (ulong)_size);
                BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(24), 3); // HAS_FLAGS | READ_ONLY
                await stream.WriteAsync(hello, _stop.Token);
                var header = new byte[28];
                var buffer = new byte[64 * 1024];
                while (!_stop.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x25609513) return;
                    var command = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
                    var offset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(16));
                    var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
                    if (command == 2) return;
                    if (count > 32 * 1024 * 1024) return;
                    var reply = new byte[16];
                    BinaryPrimitives.WriteUInt32BigEndian(reply, 0x67446698);
                    header.AsSpan(8, 8).CopyTo(reply.AsSpan(8));
                    uint error = command != 0 ? 1u : BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) != 0 ||
                        offset > (ulong)_size || count > (ulong)_size - offset ? 22u : 0u;
                    if (command == 1)
                    {
                        for (long left = count; left > 0;)
                        {
                            var n = (int)Math.Min(left, buffer.Length);
                            await stream.ReadExactlyAsync(buffer.AsMemory(0, n), idle.Token); left -= n;
                        }
                    }
                    if (error != 0 || count == 0)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), error);
                        await stream.WriteAsync(reply, idle.Token); continue;
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Get, _media);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue((long)offset, (long)(offset + count - 1));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != (long)offset || range.To != (long)(offset + count - 1) || range.Length != _size)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 5); // EIO, never download a full-image fallback
                        await stream.WriteAsync(reply, idle.Token); continue;
                    }
                    await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
                    await stream.WriteAsync(reply, idle.Token);
                    for (long left = count; left > 0;)
                    {
                        var n = (int)Math.Min(left, buffer.Length);
                        await source.ReadExactlyAsync(buffer.AsMemory(0, n), idle.Token);
                        await stream.WriteAsync(buffer.AsMemory(0, n), idle.Token); left -= n;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
            finally { _slots.Release(); }
    }

    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop(); _http.Dispose();
    }
}
