using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Microsoft.Extensions.Logging;

namespace Everboot.Services.Iscsi;

/// <summary>
/// One iSCSI session per TCP connection. Drives the login PDU exchange and
/// then loops on SCSI commands until logout or disconnect. Read-only: any
/// command that would modify data is rejected with CHECK CONDITION.
/// </summary>
internal sealed class IscsiSession
{
    private readonly TcpClient _client;
    private readonly IscsiCatalog _catalog;
    private readonly IscsiOptions _options;
    private readonly ILogger _logger;

    private uint _statSn;
    private uint _expCmdSn;
    private IscsiTarget? _target;
    private IscsiSessionType _sessionType = IscsiSessionType.Normal;
    private string? _initiatorName;
    private string? _targetName;
    private int _maxRecvDataSegment = 8192; // initiator side, what we send back limited to
    private byte[] _sense = Array.Empty<byte>();

    public IscsiSession(TcpClient client, IscsiCatalog catalog, IscsiOptions options, ILogger logger)
    {
        _client = client;
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var remote = _client.Client.RemoteEndPoint;
        _logger.LogInformation("iSCSI connection from {Remote}", remote);

        try
        {
            using var stream = _client.GetStream();
            stream.ReadTimeout = 60_000;

            if (!await LoginAsync(stream, stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            await CommandLoopAsync(stream, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "iSCSI client {Remote} disconnected", remote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "iSCSI session for {Remote} crashed", remote);
        }
        finally
        {
            _logger.LogInformation("iSCSI connection from {Remote} closed", remote);
            try { _client.Close(); } catch { }
        }
    }

    // ---------------------------------------------------------------------
    // Login
    // ---------------------------------------------------------------------

    private async Task<bool> LoginAsync(NetworkStream stream, CancellationToken ct)
    {
        // Login can take multiple PDUs as the initiator transits Security ->
        // Operational -> FullFeature. We accept being driven; we never assert
        // a Security stage (we offer AuthMethod=None outright).
        while (true)
        {
            var request = await IscsiPdu.ReadAsync(stream, ct).ConfigureAwait(false);
            if (request is null)
            {
                return false;
            }
            if (request.Opcode != IscsiOpcode.LoginRequest)
            {
                _logger.LogWarning("iSCSI: expected LoginRequest, got {Opcode}", request.Opcode);
                return false;
            }

            var requestText = ParseTextKeys(request.Data);
            if (requestText.TryGetValue("InitiatorName", out var iname))
            {
                _initiatorName = iname;
            }
            if (requestText.TryGetValue("TargetName", out var tname))
            {
                _targetName = tname;
            }
            if (requestText.TryGetValue("SessionType", out var st) &&
                string.Equals(st, "Discovery", StringComparison.OrdinalIgnoreCase))
            {
                _sessionType = IscsiSessionType.Discovery;
            }
            if (requestText.TryGetValue("MaxRecvDataSegmentLength", out var mrdsl) &&
                int.TryParse(mrdsl, out var parsed))
            {
                _maxRecvDataSegment = Math.Clamp(parsed, 512, _options.MaxReceiveDataSegment);
            }

            var requestFlags = request.FlagsByte;
            var currentStage = (requestFlags >> 2) & 0x03;
            var nextStage = requestFlags & 0x03;
            var transit = (requestFlags & 0x80) != 0;
            var requestStatSn = BinaryPrimitives.ReadUInt32BigEndian(request.Bhs.AsSpan(28, 4));
            _statSn = requestStatSn;
            _expCmdSn = BinaryPrimitives.ReadUInt32BigEndian(request.Bhs.AsSpan(32, 4));

            var responseKeys = BuildLoginResponseKeys(requestText);

            var response = new IscsiPdu
            {
                Opcode = IscsiOpcode.LoginResponse,
                Data = EncodeTextKeys(responseKeys),
            };
            // Copy ISID + TSIH from request (preserve session identity)
            request.Bhs.AsSpan(8, 8).CopyTo(response.Bhs.AsSpan(8, 8));
            // Version-active = 0x00, Version-max = 0x00 (already zero)
            response.InitiatorTaskTag = request.InitiatorTaskTag;

            // Status: 0x0000 = success
            // Flags byte: T (transit) + CSG/NSG matching the request
            byte responseFlags = (byte)((currentStage & 0x03) << 2 | (nextStage & 0x03));
            if (transit)
            {
                responseFlags |= 0x80; // T bit
            }
            response.FlagsByte = responseFlags;

            response.StatSN = _statSn;
            _statSn++;
            response.ExpCmdSN = _expCmdSn;
            response.MaxCmdSN = _expCmdSn + 16; // small command window

            await response.WriteAsync(stream, ct).ConfigureAwait(false);

            if (transit && nextStage == 3)
            {
                // FullFeaturePhase reached.
                if (_sessionType == IscsiSessionType.Normal)
                {
                    _target = _targetName is null ? null : _catalog.Find(_targetName);
                    if (_target is null)
                    {
                        _logger.LogWarning("iSCSI: initiator {Initiator} requested unknown target '{Target}'",
                            _initiatorName, _targetName);
                        return false;
                    }
                    _logger.LogInformation("iSCSI: {Initiator} logged in to target {Target} ({File})",
                        _initiatorName, _target.Iqn, Path.GetFileName(_target.FilePath));
                }
                else
                {
                    _logger.LogInformation("iSCSI: {Initiator} entered discovery session", _initiatorName);
                }
                return true;
            }
        }
    }

    private Dictionary<string, string> BuildLoginResponseKeys(Dictionary<string, string> requestKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AuthMethod"] = "None",
            ["HeaderDigest"] = "None",
            ["DataDigest"] = "None",
            ["DefaultTime2Wait"] = "0",
            ["DefaultTime2Retain"] = "0",
            ["ErrorRecoveryLevel"] = "0",
            ["IFMarker"] = "No",
            ["OFMarker"] = "No",
            ["InitialR2T"] = "Yes",
            ["ImmediateData"] = "Yes",
            ["DataPDUInOrder"] = "Yes",
            ["DataSequenceInOrder"] = "Yes",
            ["MaxOutstandingR2T"] = "1",
            ["MaxConnections"] = "1",
            ["MaxBurstLength"] = _options.MaxReceiveDataSegment.ToString(),
            ["FirstBurstLength"] = "65536",
            ["MaxRecvDataSegmentLength"] = _options.MaxReceiveDataSegment.ToString(),
        };

        // Only echo keys the initiator actually negotiated this round - per RFC 7143
        // we shouldn't include unrelated keys, but extra ones are tolerated by iPXE.
        if (requestKeys.ContainsKey("TargetName"))
        {
            result["TargetPortalGroupTag"] = "1";
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // Command loop
    // ---------------------------------------------------------------------

    private async Task CommandLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pdu = await IscsiPdu.ReadAsync(stream, ct).ConfigureAwait(false);
            if (pdu is null)
            {
                return;
            }

            switch (pdu.Opcode)
            {
                case IscsiOpcode.NopOut:
                    await HandleNopAsync(stream, pdu, ct).ConfigureAwait(false);
                    break;

                case IscsiOpcode.TextRequest:
                    await HandleTextAsync(stream, pdu, ct).ConfigureAwait(false);
                    break;

                case IscsiOpcode.ScsiCommand:
                    await HandleScsiCommandAsync(stream, pdu, ct).ConfigureAwait(false);
                    break;

                case IscsiOpcode.LogoutRequest:
                    await HandleLogoutAsync(stream, pdu, ct).ConfigureAwait(false);
                    return;

                default:
                    _logger.LogDebug("iSCSI: unhandled opcode 0x{Op:X2}", (byte)pdu.Opcode);
                    break;
            }
        }
    }

    private async Task HandleNopAsync(NetworkStream stream, IscsiPdu pdu, CancellationToken ct)
    {
        if (pdu.InitiatorTaskTag == 0xFFFFFFFFu)
        {
            // Unsolicited NopOut with tag 0xFFFFFFFF = no response wanted.
            return;
        }
        var response = new IscsiPdu
        {
            Opcode = IscsiOpcode.NopIn,
            InitiatorTaskTag = pdu.InitiatorTaskTag,
        };
        response.FlagsByte = 0x80;
        response.StatSN = _statSn;
        response.ExpCmdSN = _expCmdSn;
        response.MaxCmdSN = _expCmdSn + 16;
        await response.WriteAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task HandleTextAsync(NetworkStream stream, IscsiPdu pdu, CancellationToken ct)
    {
        var requested = ParseTextKeys(pdu.Data);

        var responseKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requested.ContainsKey("SendTargets"))
        {
            foreach (var target in _catalog.AllTargets)
            {
                responseKeys[$"TargetName"] = target.Iqn;
                // The conventional response shape is one TargetName per line
                // followed by TargetAddress lines. iPXE just needs TargetName.
            }
            if (_catalog.AllTargets.Count == 0)
            {
                responseKeys["TargetName"] = string.Empty;
            }
        }

        var response = new IscsiPdu
        {
            Opcode = IscsiOpcode.TextResponse,
            InitiatorTaskTag = pdu.InitiatorTaskTag,
            Data = EncodeSendTargetsResponse(requested.ContainsKey("SendTargets")
                ? _catalog.AllTargets.Select(t => t.Iqn)
                : Array.Empty<string>()),
        };
        response.FlagsByte = 0x80; // F bit
        response.StatSN = _statSn;
        _statSn++;
        response.ExpCmdSN = _expCmdSn;
        response.MaxCmdSN = _expCmdSn + 16;
        await response.WriteAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task HandleLogoutAsync(NetworkStream stream, IscsiPdu pdu, CancellationToken ct)
    {
        var response = new IscsiPdu
        {
            Opcode = IscsiOpcode.LogoutResponse,
            InitiatorTaskTag = pdu.InitiatorTaskTag,
        };
        response.FlagsByte = 0x80;
        response.StatSN = _statSn;
        _statSn++;
        response.ExpCmdSN = _expCmdSn;
        response.MaxCmdSN = _expCmdSn + 16;
        await response.WriteAsync(stream, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // SCSI command dispatch
    // ---------------------------------------------------------------------

    private async Task HandleScsiCommandAsync(NetworkStream stream, IscsiPdu pdu, CancellationToken ct)
    {
        // SCSI Command PDU: bytes 32-47 of BHS contain a 16-byte CDB.
        var cdb = pdu.Bhs.AsSpan(32, 16);
        var op = cdb[0];

        if (_target is null)
        {
            await SendScsiResponseAsync(stream, pdu, ScsiStatus.CheckCondition,
                BuildSense(ScsiSense.NotReady, ScsiSense.LbaOutOfRange), ct).ConfigureAwait(false);
            return;
        }

        switch (op)
        {
            case ScsiOpcode.TestUnitReady:
                await SendScsiResponseAsync(stream, pdu, ScsiStatus.Good, null, ct).ConfigureAwait(false);
                return;

            case ScsiOpcode.RequestSense:
                await SendDataInThenStatusAsync(stream, pdu, _sense.Length > 0 ? _sense : BuildSense(ScsiSense.NoSense, 0), ct).ConfigureAwait(false);
                _sense = Array.Empty<byte>();
                return;

            case ScsiOpcode.Inquiry:
                {
                    var evpd = (cdb[1] & 0x01) != 0;
                    if (evpd)
                    {
                        await SendDataInThenStatusAsync(stream, pdu, BuildVpdPage(cdb[2]), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendDataInThenStatusAsync(stream, pdu, _target.BuildStandardInquiry(), ct).ConfigureAwait(false);
                    }
                    return;
                }

            case ScsiOpcode.ReadCapacity10:
                await SendDataInThenStatusAsync(stream, pdu, _target.BuildReadCapacity10(), ct).ConfigureAwait(false);
                return;

            case ScsiOpcode.ServiceActionIn16:
                {
                    var sa = (byte)(cdb[1] & 0x1F);
                    if (sa == 0x10)
                    {
                        await SendDataInThenStatusAsync(stream, pdu, _target.BuildReadCapacity16(), ct).ConfigureAwait(false);
                        return;
                    }
                    await SendCheckConditionAsync(stream, pdu, ScsiSense.IllegalRequest, ScsiSense.InvalidCommandOperationCode, ct).ConfigureAwait(false);
                    return;
                }

            case ScsiOpcode.ModeSense6:
            case ScsiOpcode.ModeSense10:
                await SendDataInThenStatusAsync(stream, pdu, _target.BuildModeSense6(), ct).ConfigureAwait(false);
                return;

            case ScsiOpcode.ReportLuns:
                await SendDataInThenStatusAsync(stream, pdu, _target.BuildReportLuns(), ct).ConfigureAwait(false);
                return;

            case ScsiOpcode.PreventAllowMediumRemoval:
            case ScsiOpcode.StartStopUnit:
            case ScsiOpcode.SynchronizeCache10:
            case ScsiOpcode.Verify10:
                await SendScsiResponseAsync(stream, pdu, ScsiStatus.Good, null, ct).ConfigureAwait(false);
                return;

            case ScsiOpcode.Read10:
                {
                    var lba = (long)BinaryPrimitives.ReadUInt32BigEndian(cdb.Slice(2, 4));
                    var length = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(7, 2));
                    await HandleReadAsync(stream, pdu, lba, length, ct).ConfigureAwait(false);
                    return;
                }

            case ScsiOpcode.Read12:
                {
                    var lba = (long)BinaryPrimitives.ReadUInt32BigEndian(cdb.Slice(2, 4));
                    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(cdb.Slice(6, 4));
                    await HandleReadAsync(stream, pdu, lba, length, ct).ConfigureAwait(false);
                    return;
                }

            case ScsiOpcode.Read16:
                {
                    var lba = (long)BinaryPrimitives.ReadUInt64BigEndian(cdb.Slice(2, 8));
                    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(cdb.Slice(10, 4));
                    await HandleReadAsync(stream, pdu, lba, length, ct).ConfigureAwait(false);
                    return;
                }

            default:
                _logger.LogDebug("iSCSI: rejecting SCSI op 0x{Op:X2}", op);
                await SendCheckConditionAsync(stream, pdu, ScsiSense.IllegalRequest, ScsiSense.InvalidCommandOperationCode, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleReadAsync(NetworkStream stream, IscsiPdu pdu, long lba, int blockCount, CancellationToken ct)
    {
        if (_target is null)
        {
            await SendCheckConditionAsync(stream, pdu, ScsiSense.NotReady, ScsiSense.LbaOutOfRange, ct).ConfigureAwait(false);
            return;
        }

        if (lba < 0 || lba + blockCount > _target.LastLba + 1)
        {
            await SendCheckConditionAsync(stream, pdu, ScsiSense.IllegalRequest, ScsiSense.LbaOutOfRange, ct).ConfigureAwait(false);
            return;
        }

        if (blockCount == 0)
        {
            await SendScsiResponseAsync(stream, pdu, ScsiStatus.Good, null, ct).ConfigureAwait(false);
            return;
        }

        var totalBytes = (long)blockCount * _target.BlockSize;
        var offset = lba * _target.BlockSize;

        await using var file = _target.OpenRead();
        file.Seek(offset, SeekOrigin.Begin);

        var chunk = _maxRecvDataSegment;
        var remaining = totalBytes;
        var dataSn = 0u;

        while (remaining > 0)
        {
            var thisChunk = (int)Math.Min(remaining, chunk);
            var buffer = new byte[thisChunk];
            var read = 0;
            while (read < thisChunk)
            {
                var n = await file.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
            if (read < thisChunk)
            {
                Array.Resize(ref buffer, read);
            }

            var dataIn = new IscsiPdu
            {
                Opcode = IscsiOpcode.ScsiDataIn,
                Data = buffer,
                InitiatorTaskTag = pdu.InitiatorTaskTag,
            };

            var isFinal = (remaining - buffer.Length) <= 0;
            byte flags = 0;
            if (isFinal)
            {
                flags |= 0x80; // F bit
                flags |= 0x01; // S bit - status piggyback
            }
            dataIn.FlagsByte = flags;

            // Data-In specific fields
            BinaryPrimitives.WriteUInt32BigEndian(dataIn.Bhs.AsSpan(36, 4), dataSn);            // DataSN
            BinaryPrimitives.WriteUInt32BigEndian(dataIn.Bhs.AsSpan(40, 4), (uint)(totalBytes - remaining)); // BufferOffset
            BinaryPrimitives.WriteUInt32BigEndian(dataIn.Bhs.AsSpan(44, 4), 0);                  // Residual

            if (isFinal)
            {
                dataIn.StatSN = _statSn;
                _statSn++;
                dataIn.ExpCmdSN = _expCmdSn;
                dataIn.MaxCmdSN = _expCmdSn + 16;
                // Status byte in lo-byte of Bhs[3]
                dataIn.Bhs[3] = (byte)ScsiStatus.Good;
            }

            await dataIn.WriteAsync(stream, ct).ConfigureAwait(false);

            remaining -= buffer.Length;
            dataSn++;
            if (read < thisChunk)
            {
                break;
            }
        }

        // When status was piggybacked above, no separate SCSI Response PDU is needed.
    }

    // ---------------------------------------------------------------------
    // SCSI helpers
    // ---------------------------------------------------------------------

    private async Task SendDataInThenStatusAsync(NetworkStream stream, IscsiPdu cmdPdu, byte[] data, CancellationToken ct)
    {
        // Honour the allocation length from the CDB so we don't overshoot.
        var requested = GetExpectedDataLength(cmdPdu);
        if (requested > 0 && requested < data.Length)
        {
            data = data.AsSpan(0, requested).ToArray();
        }

        var dataIn = new IscsiPdu
        {
            Opcode = IscsiOpcode.ScsiDataIn,
            Data = data,
            InitiatorTaskTag = cmdPdu.InitiatorTaskTag,
        };
        // F + S bits - final & status piggyback
        dataIn.FlagsByte = 0x80 | 0x01;
        dataIn.StatSN = _statSn;
        _statSn++;
        dataIn.ExpCmdSN = _expCmdSn;
        dataIn.MaxCmdSN = _expCmdSn + 16;
        dataIn.Bhs[3] = (byte)ScsiStatus.Good;
        BinaryPrimitives.WriteUInt32BigEndian(dataIn.Bhs.AsSpan(36, 4), 0); // DataSN
        BinaryPrimitives.WriteUInt32BigEndian(dataIn.Bhs.AsSpan(40, 4), 0); // BufferOffset
        BinaryPrimitives.WriteUInt32BigEndian(dataIn.Bhs.AsSpan(44, 4), 0); // Residual

        await dataIn.WriteAsync(stream, ct).ConfigureAwait(false);
    }

    private static int GetExpectedDataLength(IscsiPdu cmdPdu)
    {
        // SCSI Command PDU: bytes 20-23 = Expected Data Transfer Length.
        return (int)BinaryPrimitives.ReadUInt32BigEndian(cmdPdu.Bhs.AsSpan(20, 4));
    }

    private async Task SendScsiResponseAsync(NetworkStream stream, IscsiPdu cmdPdu, ScsiStatus status, byte[]? sense, CancellationToken ct)
    {
        var response = new IscsiPdu
        {
            Opcode = IscsiOpcode.ScsiResponse,
            InitiatorTaskTag = cmdPdu.InitiatorTaskTag,
        };
        response.FlagsByte = 0x80; // F bit
        response.Bhs[2] = 0x00; // Response = Command Completed at Target
        response.Bhs[3] = (byte)status;

        if (sense is not null && sense.Length > 0)
        {
            var dataSegment = new byte[2 + sense.Length];
            BinaryPrimitives.WriteUInt16BigEndian(dataSegment.AsSpan(0, 2), (ushort)sense.Length);
            sense.CopyTo(dataSegment.AsSpan(2));
            response.Data = dataSegment;
        }

        response.StatSN = _statSn;
        _statSn++;
        response.ExpCmdSN = _expCmdSn;
        response.MaxCmdSN = _expCmdSn + 16;

        await response.WriteAsync(stream, ct).ConfigureAwait(false);
    }

    private Task SendCheckConditionAsync(NetworkStream stream, IscsiPdu cmdPdu, byte senseKey, ushort ascAscq, CancellationToken ct)
    {
        var sense = BuildSense(senseKey, ascAscq);
        _sense = sense;
        return SendScsiResponseAsync(stream, cmdPdu, ScsiStatus.CheckCondition, sense, ct);
    }

    private static byte[] BuildSense(byte senseKey, ushort ascAscq)
    {
        // Fixed-format sense data (18 bytes), SPC-4 §4.5.3.
        var sense = new byte[18];
        sense[0] = 0x70;                       // current errors, no information
        sense[2] = senseKey;
        sense[7] = 10;                         // additional sense length
        sense[12] = (byte)((ascAscq >> 8) & 0xFF); // ASC
        sense[13] = (byte)(ascAscq & 0xFF);   // ASCQ
        return sense;
    }

    private static byte[] BuildVpdPage(byte page)
    {
        // We respond to the bare minimum: 0x00 (supported pages) and 0x80
        // (unit serial number). Anything else gets a single-byte stub.
        switch (page)
        {
            case 0x00:
                {
                    var buf = new byte[8];
                    buf[1] = 0x00; // page code
                    buf[3] = 4;    // pages list length
                    buf[4] = 0x00;
                    buf[5] = 0x80;
                    buf[6] = 0x83;
                    buf[7] = 0xB0;
                    return buf;
                }
            case 0x80:
                {
                    var serial = Encoding.ASCII.GetBytes("EVERBOOT0001");
                    var buf = new byte[4 + serial.Length];
                    buf[1] = 0x80;
                    buf[3] = (byte)serial.Length;
                    serial.CopyTo(buf.AsSpan(4));
                    return buf;
                }
            default:
                return new byte[] { 0, page, 0, 0 };
        }
    }

    // ---------------------------------------------------------------------
    // Text key encoding
    // ---------------------------------------------------------------------

    private static Dictionary<string, string> ParseTextKeys(byte[] data)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.Length == 0)
        {
            return dict;
        }
        var entries = Encoding.ASCII.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            dict[entry[..eq]] = entry[(eq + 1)..];
        }
        return dict;
    }

    private static byte[] EncodeTextKeys(Dictionary<string, string> keys)
    {
        if (keys.Count == 0)
        {
            return Array.Empty<byte>();
        }
        var ms = new MemoryStream();
        foreach (var (k, v) in keys)
        {
            var entry = $"{k}={v}";
            var bytes = Encoding.ASCII.GetBytes(entry);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    private static byte[] EncodeSendTargetsResponse(IEnumerable<string> iqns)
    {
        // RFC 7143 §11.3 - one TargetName per record, no TargetAddress = use
        // the same portal the discovery happened on (iPXE handles this).
        var ms = new MemoryStream();
        foreach (var iqn in iqns)
        {
            var line = Encoding.ASCII.GetBytes($"TargetName={iqn}");
            ms.Write(line, 0, line.Length);
            ms.WriteByte(0);
        }
        return ms.ToArray();
    }
}
