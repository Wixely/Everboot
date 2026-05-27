using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services.Smb;

/// <summary>
/// SMB2 (2.0.2 dialect) read-only file share over TCP. One share per ISO in
/// the boot catalog, share name = slug, backed by DiscUtils streaming bytes
/// out of the original image on demand. Intended for Windows Setup running
/// inside WinPE to find <c>sources/install.wim</c> on a network share
/// without the user having to bring their own SMB server.
/// </summary>
internal sealed class SmbService : BackgroundService
{
    private readonly ILogger<SmbService> _logger;
    private readonly SmbShareCatalog _catalog;
    private readonly SmbOptions _options;

    public SmbService(ILogger<SmbService> logger, SmbShareCatalog catalog, IOptions<EverbootOptions> options)
    {
        _logger = logger;
        _catalog = catalog;
        _options = options.Value.Smb;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SMB target disabled via configuration");
            return;
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(_options.BindAddress), _options.Port);
        var listener = new TcpListener(endpoint);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogCritical(ex,
                "SMB target failed to bind {Endpoint}. Port 445 is privileged AND on Windows the " +
                "LanmanServer service holds it - stop it (Stop-Service LanmanServer) or use a higher port for testing.",
                endpoint);
            throw;
        }

        _logger.LogInformation("SMB target listening on {Endpoint}; {ShareCount} share(s)",
            endpoint, _catalog.ShareNames.Count);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                client.NoDelay = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var session = new Smb2Session(client, _catalog, _options, _logger);
                        await session.RunAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SMB session crashed");
                    }
                }, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
            _logger.LogInformation("SMB target stopped");
        }
    }
}
