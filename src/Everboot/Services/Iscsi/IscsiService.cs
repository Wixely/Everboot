using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services.Iscsi;

/// <summary>
/// Accepts iSCSI TCP connections, hands each to a fresh <see cref="IscsiSession"/>.
/// Listens on the standard port 3260 by default. Read-only, single-LUN
/// targets - whatever's in <c>data/isos/</c>.
/// </summary>
internal sealed class IscsiService : BackgroundService
{
    private readonly ILogger<IscsiService> _logger;
    private readonly IscsiCatalog _catalog;
    private readonly IscsiOptions _options;

    public IscsiService(ILogger<IscsiService> logger, IscsiCatalog catalog, IOptions<EverbootOptions> options)
    {
        _logger = logger;
        _catalog = catalog;
        _options = options.Value.Iscsi;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("iSCSI target disabled via configuration");
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
            _logger.LogCritical(ex, "iSCSI target failed to bind {Endpoint}", endpoint);
            throw;
        }

        _logger.LogInformation("iSCSI target listening on {Endpoint}, IQN base '{Iqn}'", endpoint, _options.IqnBase);

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
                        var session = new IscsiSession(client, _catalog, _options, _logger);
                        await session.RunAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "iSCSI session crashed");
                    }
                }, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
            _logger.LogInformation("iSCSI target stopped");
        }
    }
}
