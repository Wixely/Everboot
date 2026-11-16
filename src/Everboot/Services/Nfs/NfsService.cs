using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Everboot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services.Nfs;

internal sealed class NfsService(BootCatalog catalog, IOptions<EverbootOptions> options, NfsStatus status, ILogger<NfsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value.Nfs;
        if (!config.Enabled) return;
        using var protocol = new NfsProtocol(catalog, config, status);
        using var slots = new SemaphoreSlim(config.MaxConnections);
        var listeners = new[]
        {
            (Listener: new TcpListener(IPAddress.Parse(config.BindAddress), config.Port), Program: 100003u),
            (Listener: new TcpListener(IPAddress.Parse(config.BindAddress), config.MountPort), Program: 100005u),
            (Listener: new TcpListener(IPAddress.Parse(config.BindAddress), config.PortmapperPort), Program: 100000u),
        };
        try
        {
            foreach (var item in listeners)
            {
                try { item.Listener.Start(config.MaxConnections); }
                catch (SocketException ex)
                {
                    logger.LogCritical(ex, "Cannot start NFS RPC program {Program} on {Endpoint}. Check port conflicts/permissions or disable Everboot:Nfs:Enabled.",
                        item.Program, item.Listener.LocalEndpoint);
                    throw;
                }
            }
            status.Running = true;
            logger.LogInformation("Experimental read-only NFSv3 ready on TCP {Port}; mount {MountPort}; portmapper {PortmapperPort}",
                config.Port, config.MountPort, config.PortmapperPort);
            await Task.WhenAll(listeners.Select(item => AcceptAsync(item.Listener, item.Program, protocol, slots, stoppingToken)));
        }
        finally { status.Running = false; foreach (var item in listeners) item.Listener.Stop(); }
    }

    private async Task AcceptAsync(TcpListener listener, uint program, NfsProtocol protocol, SemaphoreSlim slots, CancellationToken ct)
    {
        var sessions = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(ServeAsync(client, program, protocol, slots, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(sessions); }
    }

    private async Task ServeAsync(TcpClient client, uint program, NfsProtocol protocol, SemaphoreSlim slots, CancellationToken ct)
    {
        using (client)
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var record = await ReadRecordAsync(stream, timeout.Token);
                if (record is null) break;
                byte[] reply;
                try { reply = protocol.Dispatch(record, program); }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "NFS image read failed");
                    break; // force a retry; never send a success reply with incomplete data
                }
                var marker = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(marker, 0x80000000u | (uint)reply.Length);
                await stream.WriteAsync(marker, timeout.Token);
                await stream.WriteAsync(reply, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException)
        { logger.LogDebug(ex, "NFS connection closed"); }
        finally { slots.Release(); }
    }

    internal static async Task<byte[]?> ReadRecordAsync(Stream stream, CancellationToken ct)
    {
        using var record = new MemoryStream();
        var marker = new byte[4];
        for (var fragments = 0; fragments < 128; fragments++)
        {
            var first = await stream.ReadAsync(marker.AsMemory(0, 1), ct);
            if (first == 0 && record.Length == 0 && fragments == 0) return null;
            if (first == 0) throw new EndOfStreamException();
            await stream.ReadExactlyAsync(marker.AsMemory(1), ct);
            var value = BinaryPrimitives.ReadUInt32BigEndian(marker);
            var length = value & 0x7fffffffu;
            if (length > 1024 * 1024 - record.Length) throw new InvalidDataException("RPC record exceeds 1 MiB.");
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, ct);
            record.Write(bytes);
            if ((value & 0x80000000u) != 0) return record.ToArray();
        }
        throw new InvalidDataException("Too many RPC fragments.");
    }
}
