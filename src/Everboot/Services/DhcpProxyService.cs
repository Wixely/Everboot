using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everboot.Services;

/// <summary>
/// ProxyDHCP for PXE. Listens on UDP/67 for DHCPDISCOVER packets where the
/// vendor class identifier (option 60) begins with "PXEClient", and answers
/// with a DHCPOFFER that fills in <c>siaddr</c> and the boot file - without
/// allocating an IP. Real DHCP keeps doing its job; we just chip in PXE info.
///
/// Also branches:
///   - PXE firmware (option 60 = "PXEClient") -> arch-specific iPXE loader
///   - iPXE     (option 77 = "iPXE")       -> http://server/boot.ipxe
/// </summary>
internal sealed class DhcpProxyService : BackgroundService
{
    private readonly ILogger<DhcpProxyService> _logger;
    private readonly DhcpOptions _dhcp;
    private readonly HttpOptions _http;
    private string? _resolvedServerIp;

    public DhcpProxyService(ILogger<DhcpProxyService> logger, IOptions<EverbootOptions> options)
    {
        _logger = logger;
        _dhcp = options.Value.Dhcp;
        _http = options.Value.Http;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_dhcp.Enabled)
        {
            _logger.LogInformation("DHCP proxy disabled via configuration");
            return;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.EnableBroadcast = true;

        var endpoint = new IPEndPoint(IPAddress.Parse(_dhcp.BindAddress), _dhcp.Port);
        try
        {
            socket.Bind(endpoint);
        }
        catch (SocketException ex)
        {
            _logger.LogCritical(ex,
                "DHCP proxy failed to bind {Endpoint}. Port {Port} is privileged (<1024) - " +
                "needs admin/root or CAP_NET_BIND_SERVICE. Set Everboot:Dhcp:Enabled=false " +
                "or pick a higher port to silence this in environments without PXE clients.",
                endpoint, _dhcp.Port);
            throw;
        }

        _resolvedServerIp = ResolveServerIp();
        _logger.LogInformation("DHCP proxy listening on {Endpoint}, advertising server {Server}",
            endpoint, _resolvedServerIp);

        var buffer = new byte[1500];
        var remoteEp = new IPEndPoint(IPAddress.Any, 0) as EndPoint;

        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "DHCP proxy receive failed");
                continue;
            }

            try
            {
                await HandleAsync(buffer.AsMemory(0, received.ReceivedBytes), (IPEndPoint)received.RemoteEndPoint, socket, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DHCP proxy handler crashed for packet from {Remote}", received.RemoteEndPoint);
            }
        }

        _logger.LogInformation("DHCP proxy stopped");
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> data, IPEndPoint sender, Socket socket, CancellationToken ct)
    {
        if (!DhcpPacket.TryParse(data.Span, out var request))
        {
            return;
        }
        if (request.Op != DhcpOp.BootRequest)
        {
            return;
        }
        if (request.MessageType != DhcpMessageType.Discover && request.MessageType != DhcpMessageType.Request)
        {
            return;
        }

        var vendorClass = request.VendorClassIdentifier;
        if (vendorClass is null || !vendorClass.StartsWith("PXEClient", StringComparison.Ordinal))
        {
            // Plain old DHCP client - leave it for the real DHCP server.
            return;
        }

        var arch = request.ClientArchitecture ?? DhcpClientArch.IntelX86Pc;
        var family = ClassifyArch(arch);
        var isIpxe = string.Equals(request.UserClass, "iPXE", StringComparison.Ordinal);

        var serverIp = _resolvedServerIp ?? ResolveServerIp();
        var bootFile = SelectBootFile(family, isIpxe, serverIp);

        var reply = BuildOffer(request, IPAddress.Parse(serverIp), bootFile, request.MessageType.Value);
        var replyBytes = reply.Serialize();

        var dest = ChooseReplyEndpoint(request, sender);
        try
        {
            await socket.SendToAsync(replyBytes, SocketFlags.None, dest, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "DHCP proxy reply to {Dest} failed", dest);
            return;
        }

        _logger.LogInformation(
            "PXE {ReplyType} -> {Dest}: mac={Mac} arch={Arch}({Family}) ipxe={IsIPxe} file={File}",
            reply.MessageType, dest, request.MacAddress, arch, family, isIpxe, bootFile);
    }

    private DhcpPacket BuildOffer(DhcpPacket request, IPAddress serverIp, string bootFile, DhcpMessageType requestType)
    {
        var reply = new DhcpPacket
        {
            Op = DhcpOp.BootReply,
            HType = request.HType,
            HLen = request.HLen,
            Hops = 0,
            Xid = request.Xid,
            Secs = 0,
            Flags = request.Flags,
            Ciaddr = IPAddress.Any,
            Yiaddr = IPAddress.Any,
            Siaddr = serverIp,
            Giaddr = request.Giaddr,
            Chaddr = request.Chaddr,
            Sname = "everboot",
            File = bootFile,
        };

        var responseType = requestType == DhcpMessageType.Request
            ? DhcpMessageType.Ack
            : DhcpMessageType.Offer;

        reply.SetOption(DhcpOption.MessageType, (byte)responseType);
        reply.SetOption(DhcpOption.ServerIdentifier, serverIp);
        reply.SetOption(DhcpOption.VendorClassIdentifier, "PXEClient");
        reply.SetOption(DhcpOption.TftpServerName, serverIp.ToString());
        reply.SetOption(DhcpOption.BootFileName, bootFile);

        return reply;
    }

    private IPEndPoint ChooseReplyEndpoint(DhcpPacket request, IPEndPoint sender)
    {
        // Dev/test: reply unicast to the actual source endpoint (handy on loopback).
        if (_dhcp.UnicastReplyToSource)
        {
            return new IPEndPoint(sender.Address, sender.Port == 0 ? 68 : sender.Port);
        }
        // Relay agent in play: send to the relay on port 67.
        if (!request.Giaddr.Equals(IPAddress.Any))
        {
            return new IPEndPoint(request.Giaddr, 67);
        }
        // No allocated IP yet -> broadcast on the client port.
        return new IPEndPoint(IPAddress.Broadcast, 68);
    }

    private string SelectBootFile(DhcpClientFamily family, bool isIpxe, string serverIp)
    {
        if (isIpxe)
        {
            return _dhcp.IpxeScriptUrl
                .Replace("{server}", $"{serverIp}:{_http.Port}", StringComparison.Ordinal);
        }
        return family switch
        {
            DhcpClientFamily.Bios => _dhcp.BootFiles.Bios,
            DhcpClientFamily.Uefi32 => _dhcp.BootFiles.Uefi32,
            DhcpClientFamily.Uefi64 => _dhcp.BootFiles.Uefi64,
            DhcpClientFamily.UefiArm64 => _dhcp.BootFiles.UefiArm64,
            _ => _dhcp.BootFiles.Uefi64,
        };
    }

    private static DhcpClientFamily ClassifyArch(ushort code) => code switch
    {
        DhcpClientArch.IntelX86Pc => DhcpClientFamily.Bios,
        DhcpClientArch.EfiIa32 => DhcpClientFamily.Uefi32,
        DhcpClientArch.HttpIa32 => DhcpClientFamily.Uefi32,
        DhcpClientArch.EfiArm64 => DhcpClientFamily.UefiArm64,
        DhcpClientArch.HttpArm64 => DhcpClientFamily.UefiArm64,
        DhcpClientArch.EfiX64 => DhcpClientFamily.Uefi64,
        DhcpClientArch.EfiBc => DhcpClientFamily.Uefi64,
        DhcpClientArch.HttpX64 => DhcpClientFamily.Uefi64,
        _ => DhcpClientFamily.Uefi64,
    };

    private string ResolveServerIp()
    {
        if (!string.IsNullOrWhiteSpace(_dhcp.ServerAddress))
        {
            return _dhcp.ServerAddress;
        }

        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            var addr = iface.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a.Address));
            if (addr is not null)
            {
                return addr.Address.ToString();
            }
        }
        return "127.0.0.1";
    }
}
