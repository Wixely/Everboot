using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Logging;

namespace Everboot.Services;

/// <summary>
/// Handles one TFTP read transfer on its own ephemeral UDP port (a "transfer
/// identifier", per RFC 1350). Lockstep send/ack with negotiated blksize +
/// tsize; bigger options like windowsize are ignored on purpose.
/// </summary>
internal sealed class TftpSession
{
    private readonly ILogger<TftpService> _logger;
    private readonly string _tftpRoot;
    private readonly TftpOptions _options;

    public TftpSession(ILogger<TftpService> logger, string tftpRoot, TftpOptions options)
    {
        _logger = logger;
        _tftpRoot = tftpRoot;
        _options = options;
    }

    public async Task RunAsync(IPEndPoint client, TftpRequest request, CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.Connect(client);

        try
        {
            if (!string.Equals(request.Mode, "octet", StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorAsync(udp, TftpErrorCode.IllegalOperation,
                    $"unsupported mode '{request.Mode}' (only octet)", stoppingToken).ConfigureAwait(false);
                return;
            }

            if (!TryResolveFile(request.Filename, out var fullPath, out var errCode, out var errMsg))
            {
                _logger.LogInformation("TFTP RRQ {File} from {Client} rejected: {Reason}",
                    request.Filename, client, errMsg);
                await SendErrorAsync(udp, errCode, errMsg, stoppingToken).ConfigureAwait(false);
                return;
            }

            await using var file = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var totalSize = file.Length;
            var blockSize = 512;
            var accepted = NegotiateOptions(request.Options, totalSize, ref blockSize);

            if (accepted.Count > 0)
            {
                _logger.LogDebug("TFTP OACK to {Client} for {File}: {Options}",
                    client, request.Filename, FormatOptions(accepted));
                if (!await SendAndAwaitAckAsync(udp, TftpProtocol.BuildOack(accepted), 0, stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("TFTP OACK ACK timed out for {Client} {File}", client, request.Filename);
                    return;
                }
            }

            var stopwatch = Stopwatch.StartNew();
            await TransferDataAsync(udp, file, blockSize, client, request.Filename, stoppingToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogInformation("TFTP sent {Bytes} bytes of {File} to {Client} in {Elapsed} ms (blksize={BlockSize})",
                totalSize, request.Filename, client, stopwatch.ElapsedMilliseconds, blockSize);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TFTP session for {File} from {Client} failed", request.Filename, client);
            try { await SendErrorAsync(udp, TftpErrorCode.NotDefined, "internal error", CancellationToken.None).ConfigureAwait(false); }
            catch { /* swallow */ }
        }
    }

    private List<KeyValuePair<string, string>> NegotiateOptions(
        IReadOnlyList<KeyValuePair<string, string>> requested,
        long fileSize,
        ref int blockSize)
    {
        var accepted = new List<KeyValuePair<string, string>>(requested.Count);

        foreach (var (rawKey, rawValue) in requested)
        {
            var key = rawKey.ToLowerInvariant();
            switch (key)
            {
                case "blksize":
                    if (int.TryParse(rawValue, out var requestedBs) && requestedBs >= 8)
                    {
                        var clamped = Math.Min(requestedBs, _options.MaxBlockSize);
                        blockSize = clamped;
                        accepted.Add(new KeyValuePair<string, string>("blksize", clamped.ToString()));
                    }
                    break;

                case "tsize":
                    accepted.Add(new KeyValuePair<string, string>("tsize", fileSize.ToString()));
                    break;

                // windowsize, timeout: deliberately not echoed (we only do lockstep).
            }
        }

        return accepted;
    }

    private async Task TransferDataAsync(
        UdpClient udp,
        FileStream file,
        int blockSize,
        IPEndPoint client,
        string filename,
        CancellationToken stoppingToken)
    {
        var sendBuffer = new byte[4 + blockSize];
        TftpProtocol.WriteDataHeader(sendBuffer, 0); // opcode goes once; block number set per iteration

        ushort blockNumber = 1;
        var done = false;

        while (!done)
        {
            BinaryPrimitives.WriteUInt16BigEndian(sendBuffer.AsSpan(2, 2), blockNumber);

            var read = await file.ReadAtLeastAsync(
                sendBuffer.AsMemory(4, blockSize),
                blockSize,
                throwOnEndOfStream: false,
                stoppingToken).ConfigureAwait(false);

            done = read < blockSize;

            var packetLength = 4 + read;
            if (!await SendAndAwaitAckAsync(udp, sendBuffer.AsMemory(0, packetLength), blockNumber, stoppingToken).ConfigureAwait(false))
            {
                _logger.LogWarning("TFTP block {Block} of {File} to {Client} timed out after {Retries} retries",
                    blockNumber, filename, client, _options.MaxRetries);
                return;
            }

            // 16-bit block number wraps after 65535 (de-facto: continue at 0).
            blockNumber = unchecked((ushort)(blockNumber + 1));
        }
    }

    private async Task<bool> SendAndAwaitAckAsync(UdpClient udp, ReadOnlyMemory<byte> packet, ushort expected, CancellationToken stoppingToken)
    {
        var packetArray = packet.ToArray();

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            await udp.SendAsync(packetArray, packetArray.Length).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(_options.BlockTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            try
            {
                while (true)
                {
                    var received = await udp.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);

                    if (TftpProtocol.IsError(received.Buffer, out var errCode, out var errMsg))
                    {
                        _logger.LogInformation("TFTP client returned error {Code}: {Message}", errCode, errMsg);
                        return false;
                    }

                    if (TftpProtocol.TryReadAck(received.Buffer, out var ackBlock) && ackBlock == expected)
                    {
                        return true;
                    }
                    // Stale ACK or unexpected packet - keep waiting for the right one within the timeout.
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // timed out waiting for this block's ACK - retransmit
            }
        }

        return false;
    }

    private async Task SendErrorAsync(UdpClient udp, TftpErrorCode code, string message, CancellationToken ct)
    {
        var packet = TftpProtocol.BuildError(code, message);
        try
        {
            await udp.SendAsync(packet, packet.Length).ConfigureAwait(false);
        }
        catch
        {
            // we tried; nothing else we can do over UDP
        }
    }

    private bool TryResolveFile(string filename, out string fullPath, out TftpErrorCode code, out string message)
    {
        fullPath = string.Empty;
        code = TftpErrorCode.NotDefined;
        message = string.Empty;

        if (string.IsNullOrEmpty(filename))
        {
            code = TftpErrorCode.IllegalOperation;
            message = "empty filename";
            return false;
        }

        var normalized = filename.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
        {
            code = TftpErrorCode.AccessViolation;
            message = "invalid path";
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_tftpRoot, normalized));
        if (!candidate.StartsWith(_tftpRoot, StringComparison.OrdinalIgnoreCase))
        {
            code = TftpErrorCode.AccessViolation;
            message = "outside tftp root";
            return false;
        }

        if (File.Exists(candidate))
        {
            fullPath = candidate;
            return true;
        }

        // Case-insensitive fallback (PXE clients are inconsistent about case;
        // Linux file systems care, so let the user keep tftp/ in whatever case).
        var dir = Path.GetDirectoryName(candidate);
        var leaf = Path.GetFileName(candidate);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            var match = Directory.EnumerateFiles(dir)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), leaf, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                fullPath = match;
                return true;
            }
        }

        code = TftpErrorCode.FileNotFound;
        message = filename;
        return false;
    }

    private static string FormatOptions(IEnumerable<KeyValuePair<string, string>> options)
    {
        return string.Join(',', options.Select(o => $"{o.Key}={o.Value}"));
    }
}
