using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services;

/// <summary>
/// TFTP read-only server (RFC 1350 + 2347/2348/2349 options).
/// Used by PXE firmware to fetch the iPXE chainload binary before HTTP takes
/// over for everything else. Files are served out of <c>data/tftp/</c>.
/// </summary>
internal sealed class TftpService : BackgroundService
{
    private readonly ILogger<TftpService> _logger;
    private readonly BootCatalog _catalog;
    private readonly TftpOptions _options;

    public TftpService(ILogger<TftpService> logger, BootCatalog catalog, IOptions<EverbootOptions> options)
    {
        _logger = logger;
        _catalog = catalog;
        _options = options.Value.Tftp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bindIp = IPAddress.Parse(_options.BindAddress);
        var endpoint = new IPEndPoint(bindIp, _options.Port);

        UdpClient listener;
        try
        {
            listener = new UdpClient(endpoint);
        }
        catch (SocketException ex)
        {
            _logger.LogCritical(ex,
                "TFTP listener failed to bind on {Endpoint}. " +
                "Port {Port} is privileged (<1024) - run as admin/root, " +
                "grant CAP_NET_BIND_SERVICE, or set Everboot:Tftp:Port to a higher value.",
                endpoint, _options.Port);
            throw;
        }

        _logger.LogInformation("TFTP server listening on {Endpoint}, root={Root}", endpoint, _catalog.TftpDirectory);

        using var semaphore = new SemaphoreSlim(_options.MaxConcurrentTransfers);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await listener.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "TFTP listener receive failed");
                    continue;
                }

                if (received.Buffer.Length < 2)
                {
                    continue;
                }

                var opcode = (TftpOpcode)BinaryPrimitives.ReadUInt16BigEndian(received.Buffer);
                if (opcode == TftpOpcode.WriteRequest)
                {
                    await ReplyErrorAsync(listener, received.RemoteEndPoint,
                        TftpErrorCode.IllegalOperation, "writes not supported", stoppingToken).ConfigureAwait(false);
                    continue;
                }
                if (opcode != TftpOpcode.ReadRequest)
                {
                    continue;
                }

                if (!TftpProtocol.TryParseRequest(received.Buffer.AsSpan(2), out var request))
                {
                    await ReplyErrorAsync(listener, received.RemoteEndPoint,
                        TftpErrorCode.IllegalOperation, "malformed request", stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!semaphore.Wait(0))
                {
                    _logger.LogWarning("TFTP server at capacity ({Cap}); rejecting {Client} {File}",
                        _options.MaxConcurrentTransfers, received.RemoteEndPoint, request.Filename);
                    await ReplyErrorAsync(listener, received.RemoteEndPoint,
                        TftpErrorCode.NotDefined, "server busy", stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogInformation("TFTP RRQ {File} (mode={Mode}, opts={Opts}) from {Client}",
                    request.Filename, request.Mode, request.Options.Count, received.RemoteEndPoint);

                var client = received.RemoteEndPoint;
                var snapshotRequest = request;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var session = new TftpSession(_logger, _catalog.TftpDirectory, _options);
                        await session.RunAsync(client, snapshotRequest, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled error in TFTP session for {Client}", client);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, stoppingToken);
            }
        }
        finally
        {
            listener.Dispose();
        }

        _logger.LogInformation("TFTP server stopped");
    }

    private async Task ReplyErrorAsync(UdpClient listener, IPEndPoint client, TftpErrorCode code, string message, CancellationToken ct)
    {
        try
        {
            var packet = TftpProtocol.BuildError(code, message);
            await listener.SendAsync(packet, packet.Length, client).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }
}
