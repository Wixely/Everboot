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
using Everboot.Services;
using Microsoft.Extensions.Logging;

namespace Everboot.Services.Smb;

/// <summary>
/// One SMB2 session = one TCP connection. Handles the SMB2 NEGOTIATE +
/// SESSION_SETUP exchange (accept-anything), TREE_CONNECT to a share named
/// after an ISO slug, and the read-only CREATE/READ/QUERY_INFO/
/// QUERY_DIRECTORY/CLOSE/etc. command loop.
///
/// Scope: SMB 2.0.2 only. No signing, no encryption, no oplocks, no leases,
/// no DFS, no multi-channel. No NTLM auth - SESSION_SETUP returns success
/// without inspecting credentials. Good enough for our own client simulator;
/// real Windows clients may want NTLM, that's the next step if needed.
/// </summary>
internal sealed class Smb2Session
{
    private readonly TcpClient _client;
    private readonly SmbShareCatalog _catalog;
    private readonly SmbOptions _options;
    private readonly ILogger _logger;

    private const ulong DefaultSessionId = 0x1000_0001;
    private const uint DefaultTreeId = 0x0000_0001;

    private readonly Dictionary<ulong, SmbHandle> _handles = new();
    private readonly Dictionary<uint, SmbShare> _trees = new();
    private ulong _nextHandleId = 1;
    private uint _nextTreeId = 1;

    public Smb2Session(TcpClient client, SmbShareCatalog catalog, SmbOptions options, ILogger logger)
    {
        _client = client;
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var remote = _client.Client.RemoteEndPoint;
        _logger.LogInformation("SMB connection from {Remote}", remote);

        try
        {
            using var stream = _client.GetStream();
            stream.ReadTimeout = 60_000;

            while (!stoppingToken.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(stream, stoppingToken).ConfigureAwait(false);
                if (packet is null)
                {
                    return;
                }

                await ProcessAsync(stream, packet, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "SMB client {Remote} disconnected", remote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMB session for {Remote} crashed", remote);
        }
        finally
        {
            foreach (var h in _handles.Values) h.Dispose();
            foreach (var s in _trees.Values) s.Dispose();
            try { _client.Close(); } catch { }
            _logger.LogInformation("SMB connection from {Remote} closed", remote);
        }
    }

    // ---------------------------------------------------------------------
    // NBT framing: 4-byte header (1 byte type, 3 bytes BE length) + payload.
    // ---------------------------------------------------------------------

    private static async Task<byte[]?> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var nbt = new byte[4];
        if (!await ReadExactAsync(stream, nbt, ct).ConfigureAwait(false))
        {
            return null;
        }
        var len = (nbt[1] << 16) | (nbt[2] << 8) | nbt[3];
        if (len == 0 || len > 16 * 1024 * 1024)
        {
            return null;
        }
        var data = new byte[len];
        if (!await ReadExactAsync(stream, data, ct).ConfigureAwait(false))
        {
            return null;
        }
        return data;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var nbt = new byte[4];
        nbt[0] = 0;
        nbt[1] = (byte)((payload.Length >> 16) & 0xFF);
        nbt[2] = (byte)((payload.Length >> 8) & 0xFF);
        nbt[3] = (byte)(payload.Length & 0xFF);
        await stream.WriteAsync(nbt, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------

    private async Task ProcessAsync(NetworkStream stream, byte[] packet, CancellationToken ct)
    {
        Smb2Header header;
        try
        {
            header = Smb2Header.Parse(packet);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed SMB2 packet");
            return;
        }

        var body = packet.AsMemory(Smb2Header.HeaderLength);

        switch (header.Command)
        {
            case Smb2Command.Negotiate:
                await HandleNegotiateAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.SessionSetup:
                await HandleSessionSetupAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Logoff:
                await SendErrorAsync(stream, header, NtStatus.Success, ct).ConfigureAwait(false);
                break;
            case Smb2Command.TreeConnect:
                await HandleTreeConnectAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.TreeDisconnect:
                await HandleTreeDisconnectAsync(stream, header, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Create:
                await HandleCreateAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Read:
                await HandleReadAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Close:
                await HandleCloseAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.QueryInfo:
                await HandleQueryInfoAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.QueryDirectory:
                await HandleQueryDirectoryAsync(stream, header, body, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Ioctl:
                await SendErrorAsync(stream, header, NtStatus.InvalidDeviceRequest, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Echo:
                await HandleEchoAsync(stream, header, ct).ConfigureAwait(false);
                break;
            case Smb2Command.Cancel:
                // Cancel produces no response.
                break;
            default:
                _logger.LogDebug("SMB: unhandled command {Command}", header.Command);
                await SendErrorAsync(stream, header, NtStatus.NotSupported, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendAsync(NetworkStream stream, Smb2Header response, byte[] body, CancellationToken ct)
    {
        var packet = new byte[Smb2Header.HeaderLength + body.Length];
        response.Buffer.CopyTo(packet, 0);
        body.CopyTo(packet, Smb2Header.HeaderLength);
        await WritePacketAsync(stream, packet, ct).ConfigureAwait(false);
    }

    private Task SendErrorAsync(NetworkStream stream, Smb2Header request, uint status, CancellationToken ct)
    {
        var response = request.BuildResponse(status);
        // Error response body (MS-SMB2 §2.2.2): 9-byte stub.
        var body = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 9);
        return SendAsync(stream, response, body, ct);
    }

    // ---------------------------------------------------------------------
    // NEGOTIATE - hard-code dialect 0x0202 (SMB 2.0.2). Negotiate signing
    // off, no capabilities, server GUID = zero.
    // ---------------------------------------------------------------------

    private async Task HandleNegotiateAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        // Request body has StructureSize=36 + dialect count + flags etc.
        // We don't bother parsing - just respond with our fixed dialect.
        var response = request.BuildResponse();

        var resp = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 65);                 // StructureSize
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(2, 2), 0);                  // SecurityMode = signing not required
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(4, 2), Smb2Dialect.Smb202); // DialectRevision
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(6, 2), 0);                  // Reserved (NegotiateContextCount)
        // 16-byte ServerGuid stays zero
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(24, 4), 0);                  // Capabilities
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(28, 4), 65536);              // MaxTransactSize
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(32, 4), (uint)_options.MaxReadSize); // MaxReadSize
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(36, 4), 65536);              // MaxWriteSize
        BinaryPrimitives.WriteUInt64LittleEndian(resp.AsSpan(40, 8), (ulong)DateTimeOffset.UtcNow.ToFileTime()); // SystemTime
        BinaryPrimitives.WriteUInt64LittleEndian(resp.AsSpan(48, 8), 0);                  // ServerStartTime
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(56, 2), 0);                  // SecurityBufferOffset
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(58, 2), 0);                  // SecurityBufferLength
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(60, 4), 0);                  // Reserved2

        await SendAsync(stream, response, resp, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // SESSION_SETUP - accept everything, no auth checks.
    // ---------------------------------------------------------------------

    private async Task HandleSessionSetupAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var response = request.BuildResponse();
        response.SessionId = DefaultSessionId;

        var resp = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 9);   // StructureSize
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(2, 2), 0);   // SessionFlags = 0
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(4, 2), 72);  // SecurityBufferOffset (header+0)
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(6, 2), 0);   // SecurityBufferLength
        // (no security buffer follows)

        await SendAsync(stream, response, resp, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // TREE_CONNECT - share name = ISO slug.
    // ---------------------------------------------------------------------

    private async Task HandleTreeConnectAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var span = body.Span;
        // StructureSize (2) + Flags (2) + PathOffset (2) + PathLength (2) + UTF-16 path
        var pathOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));
        var pathLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
        var absOffset = pathOffset - Smb2Header.HeaderLength;
        var pathBytes = span.Slice(absOffset, pathLength);
        var unc = Encoding.Unicode.GetString(pathBytes);

        // UNC like \\server\share - take the last path component as share name.
        var parts = unc.TrimStart('\\').Split('\\');
        var shareName = parts.Length > 0 ? parts[^1] : string.Empty;

        if (!_catalog.TryOpen(shareName, out var share) || share is null)
        {
            _logger.LogInformation("SMB TREE_CONNECT '{Unc}' -> bad share", unc);
            await SendErrorAsync(stream, request, NtStatus.BadNetworkName, ct).ConfigureAwait(false);
            return;
        }

        var treeId = _nextTreeId++;
        _trees[treeId] = share;

        var response = request.BuildResponse();
        response.TreeId = treeId;

        var resp = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 16);          // StructureSize
        resp[2] = ShareType.Disk;                                                 // ShareType
        resp[3] = 0;                                                              // Reserved
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(4, 4), 0);            // ShareFlags
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(8, 4), 0);            // Capabilities
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(12, 4), 0x001F00A9);  // MaximalAccess (read-ish)

        _logger.LogInformation("SMB TREE_CONNECT '{Unc}' -> share '{Share}', tid={TreeId}", unc, shareName, treeId);
        await SendAsync(stream, response, resp, ct).ConfigureAwait(false);
    }

    private async Task HandleTreeDisconnectAsync(NetworkStream stream, Smb2Header request, CancellationToken ct)
    {
        if (_trees.Remove(request.TreeId, out var share))
        {
            share.Dispose();
        }
        var resp = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 4);
        await SendAsync(stream, request.BuildResponse(), resp, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // CREATE - open a file or directory inside the share.
    // ---------------------------------------------------------------------

    private async Task HandleCreateAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!_trees.TryGetValue(request.TreeId, out var share))
        {
            await SendErrorAsync(stream, request, NtStatus.NetworkNameDeleted, ct).ConfigureAwait(false);
            return;
        }

        var span = body.Span;
        // CREATE request layout (MS-SMB2 §2.2.13):
        // StructureSize (2) + SecurityFlags(1) + RequestedOplockLevel(1) +
        // ImpersonationLevel (4) + SmbCreateFlags(8) + Reserved(8) +
        // DesiredAccess(4) + FileAttributes(4) + ShareAccess(4) +
        // CreateDisposition(4) + CreateOptions(4) +
        // NameOffset(2) + NameLength(2) + CreateContextsOffset(4) + CreateContextsLength(4) + Buffer
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(44, 2));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(46, 2));
        var absOffset = nameOffset - Smb2Header.HeaderLength;
        var name = nameLength == 0
            ? string.Empty
            : Encoding.Unicode.GetString(span.Slice(absOffset, nameLength)).Replace('\\', '/');

        var fs = share.FileSystem;
        var resolved = string.IsNullOrEmpty(name) ? "" : IsoFileResolver.Resolve(fs, name);

        bool exists;
        bool isDirectory;
        long length = 0;
        DateTimeOffset created = DateTimeOffset.UnixEpoch;
        DateTimeOffset modified = DateTimeOffset.UnixEpoch;

        if (string.IsNullOrEmpty(name))
        {
            // Opening the share root
            resolved = "";
            exists = true;
            isDirectory = true;
        }
        else if (resolved is null)
        {
            // Try directory probe explicitly - resolver only matches files.
            if (TryResolveDirectory(fs, name, out var dirResolved))
            {
                resolved = dirResolved;
                exists = true;
                isDirectory = true;
            }
            else
            {
                exists = false;
                isDirectory = false;
            }
        }
        else
        {
            exists = true;
            isDirectory = false;
        }

        if (!exists)
        {
            await SendErrorAsync(stream, request, NtStatus.ObjectNameNotFound, ct).ConfigureAwait(false);
            return;
        }

        Stream? fileStream = null;
        if (!isDirectory && !string.IsNullOrEmpty(resolved))
        {
            try
            {
                fileStream = fs.OpenFile(resolved, FileMode.Open, FileAccess.Read);
                length = fileStream.Length;
                try { modified = new DateTimeOffset(fs.GetLastWriteTimeUtc(resolved), TimeSpan.Zero); } catch { }
                try { created = new DateTimeOffset(fs.GetCreationTimeUtc(resolved), TimeSpan.Zero); } catch { modified = modified == DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : modified; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SMB CREATE failed for '{Path}'", name);
                await SendErrorAsync(stream, request, NtStatus.ObjectNameNotFound, ct).ConfigureAwait(false);
                return;
            }
        }

        var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        attrs |= FileAttributes.ReadOnly;

        var handleId = _nextHandleId++;
        var handle = new SmbHandle(
            handleId,
            resolved ?? string.Empty,
            isDirectory,
            length,
            created == DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : created,
            modified == DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : modified,
            attrs,
            fileStream);
        _handles[handleId] = handle;

        var response = request.BuildResponse();
        var resp = new byte[89];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 89);                     // StructureSize
        resp[2] = 0;                                                                          // OplockLevel
        resp[3] = 0;                                                                          // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(4, 4), 1);                       // CreateAction = OPENED
        BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(8, 8), handle.Created.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(16, 8), handle.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(24, 8), handle.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(32, 8), handle.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(40, 8), length);                  // AllocationSize
        BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(48, 8), length);                  // EndOfFile
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(56, 4), attrs);                  // FileAttributes
        // bytes 60-63 Reserved2
        BinaryPrimitives.WriteUInt64LittleEndian(resp.AsSpan(64, 8), handle.PersistentId);
        BinaryPrimitives.WriteUInt64LittleEndian(resp.AsSpan(72, 8), handle.VolatileId);
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(80, 4), 0);                      // CreateContextsOffset
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(84, 4), 0);                      // CreateContextsLength
        // byte 88: trailing zero in StructureSize=89 layout

        await SendAsync(stream, response, resp, ct).ConfigureAwait(false);
    }

    private static bool TryResolveDirectory(DiscUtils.DiscFileSystem fs, string requestPath, out string resolved)
    {
        resolved = string.Empty;
        var normalized = requestPath.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return true;
        }
        try
        {
            if (fs.DirectoryExists(normalized))
            {
                resolved = normalized;
                return true;
            }
            var backslashed = normalized.Replace('/', '\\');
            if (fs.DirectoryExists(backslashed))
            {
                resolved = backslashed;
                return true;
            }
        }
        catch { }
        return false;
    }

    // ---------------------------------------------------------------------
    // READ
    // ---------------------------------------------------------------------

    private async Task HandleReadAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var span = body.Span;
        // READ request: StructureSize(2) Padding(1) Flags(1) Length(4) Offset(8) FileId(16) MinCount(4) Channel(4) RemBytes(4) ReadChannelInfoOffset(2) ReadChannelInfoLength(2) Buffer(1)
        var length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        var offset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8, 8));
        var persistent = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8));
        var volatileId = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24, 8));

        if (!_handles.TryGetValue(volatileId, out var handle) || handle.FileStream is null)
        {
            await SendErrorAsync(stream, request, NtStatus.AccessDenied, ct).ConfigureAwait(false);
            return;
        }
        if (offset < 0 || offset > handle.Length)
        {
            await SendErrorAsync(stream, request, NtStatus.EndOfFile, ct).ConfigureAwait(false);
            return;
        }

        var maxLen = Math.Min((long)length, (long)_options.MaxReadSize);
        var available = handle.Length - offset;
        var toRead = (int)Math.Min(maxLen, available);

        if (toRead <= 0)
        {
            await SendErrorAsync(stream, request, NtStatus.EndOfFile, ct).ConfigureAwait(false);
            return;
        }

        var dataBuf = new byte[toRead];
        handle.FileStream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < toRead)
        {
            var n = await handle.FileStream.ReadAsync(dataBuf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (read < toRead) Array.Resize(ref dataBuf, read);

        var response = request.BuildResponse();
        const int respHeaderLen = 16;
        var resp = new byte[respHeaderLen + dataBuf.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 17);                          // StructureSize
        resp[2] = (byte)(Smb2Header.HeaderLength + respHeaderLen);                                 // DataOffset
        resp[3] = 0;                                                                                // Reserved
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(4, 4), (uint)dataBuf.Length);          // DataLength
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(8, 4), 0);                             // DataRemaining
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(12, 4), 0);                            // Reserved2
        dataBuf.CopyTo(resp, respHeaderLen);

        await SendAsync(stream, response, resp, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // CLOSE
    // ---------------------------------------------------------------------

    private async Task HandleCloseAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var span = body.Span;
        // StructureSize(2) Flags(2) Reserved(4) FileId{P(8) V(8)}
        var volatileId = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8));
        if (_handles.Remove(volatileId, out var handle))
        {
            handle.Dispose();
        }

        var resp = new byte[60];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 60);   // StructureSize
        // remaining fields zero
        await SendAsync(stream, request.BuildResponse(), resp, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // QUERY_INFO - file standard / basic / network-open info.
    // ---------------------------------------------------------------------

    private async Task HandleQueryInfoAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var span = body.Span;
        // StructureSize(2) InfoType(1) FileInfoClass(1) OutputBufferLength(4) ...
        var infoType = span[2];
        var fileInfoClass = span[3];
        // FileId at offset 24 (StructureSize(2)+1+1+4+2+2+4+4+4 = 24)
        var volatileId = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(32, 8));

        if (!_handles.TryGetValue(volatileId, out var handle))
        {
            await SendErrorAsync(stream, request, NtStatus.AccessDenied, ct).ConfigureAwait(false);
            return;
        }

        byte[]? payload = null;
        if (infoType == InfoType.File)
        {
            payload = fileInfoClass switch
            {
                FileInfoClass.FileBasicInformation => BuildFileBasicInfo(handle),
                FileInfoClass.FileStandardInformation => BuildFileStandardInfo(handle),
                FileInfoClass.FileNetworkOpenInformation => BuildFileNetworkOpenInfo(handle),
                FileInfoClass.FileInternalInformation => BuildFileInternalInfo(handle),
                FileInfoClass.FileAllInformation => BuildFileAllInformation(handle),
                FileInfoClass.FileEaInformation => new byte[4],   // EaSize = 0
                FileInfoClass.FilePositionInformation => new byte[8], // current position = 0
                FileInfoClass.FileAlignmentInformation => new byte[4], // no alignment
                _ => null,
            };
        }
        else if (infoType == InfoType.FileSystem)
        {
            payload = fileInfoClass switch
            {
                FileSystemInfoClass.FileFsAttributeInformation => BuildFsAttributeInfo(),
                FileSystemInfoClass.FileFsSizeInformation => BuildFsSizeInfo(),
                FileSystemInfoClass.FileFsDeviceInformation => BuildFsDeviceInfo(),
                FileSystemInfoClass.FileFsVolumeInformation => BuildFsVolumeInfo(),
                _ => null,
            };
        }

        if (payload is null)
        {
            await SendErrorAsync(stream, request, NtStatus.InvalidInfoClass, ct).ConfigureAwait(false);
            return;
        }

        await SendQueryInfoResponseAsync(stream, request, payload, ct).ConfigureAwait(false);
    }

    private Task SendQueryInfoResponseAsync(NetworkStream stream, Smb2Header request, byte[] payload, CancellationToken ct)
    {
        const int respHeaderLen = 8;
        var resp = new byte[respHeaderLen + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 9);                                       // StructureSize
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(2, 2), (ushort)(Smb2Header.HeaderLength + respHeaderLen)); // OutputBufferOffset
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(4, 4), (uint)payload.Length);                    // OutputBufferLength
        payload.CopyTo(resp, respHeaderLen);
        return SendAsync(stream, request.BuildResponse(), resp, ct);
    }

    private static byte[] BuildFileBasicInfo(SmbHandle h)
    {
        var buf = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), h.Created.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), h.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), h.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), h.Modified.ToFileTime());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32, 4), h.FileAttributes);
        return buf;
    }

    private static byte[] BuildFileStandardInfo(SmbHandle h)
    {
        var buf = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), h.Length);  // AllocationSize
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), h.Length);  // EndOfFile
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 1);        // NumberOfLinks
        buf[20] = 0;                                                            // DeletePending
        buf[21] = h.IsDirectory ? (byte)1 : (byte)0;                            // Directory
        return buf;
    }

    private static byte[] BuildFileNetworkOpenInfo(SmbHandle h)
    {
        var buf = new byte[56];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), h.Created.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), h.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), h.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), h.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32, 8), h.Length);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40, 8), h.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(48, 4), h.FileAttributes);
        return buf;
    }

    private static byte[] BuildFileInternalInfo(SmbHandle h)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), h.VolatileId);
        return buf;
    }

    private static byte[] BuildFileAllInformation(SmbHandle h)
    {
        // Basic(40) + Standard(24) + Internal(8) + EA(4) + Access(4) + Position(8) + Mode(4) + Alignment(4) + NameInformation
        var name = h.Path.Replace('/', '\\');
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var nameInfoLen = 4 + nameBytes.Length;
        var buf = new byte[40 + 24 + 8 + 4 + 4 + 8 + 4 + 4 + nameInfoLen];
        var pos = 0;
        BuildFileBasicInfo(h).CopyTo(buf, pos); pos += 40;
        BuildFileStandardInfo(h).CopyTo(buf, pos); pos += 24;
        BuildFileInternalInfo(h).CopyTo(buf, pos); pos += 8;
        pos += 4;  // EaInformation = 0
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), 0x00120089); pos += 4; // AccessFlags (read-ish)
        pos += 8;  // Position = 0
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), 0); pos += 4; // Mode
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), 0); pos += 4; // Alignment
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), (uint)nameBytes.Length); pos += 4;
        nameBytes.CopyTo(buf, pos);
        return buf;
    }

    private byte[] BuildFsAttributeInfo()
    {
        var name = Encoding.Unicode.GetBytes("NTFS");
        var buf = new byte[8 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0x000200FF); // attrs: read-only, case-preserving, etc.
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 255);         // MaximumComponentNameLength
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8 - 4, 4), (uint)name.Length); // overwritten below
        // Re-lay out properly:
        var realBuf = new byte[12 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(realBuf.AsSpan(0, 4), 0x000200FF);
        BinaryPrimitives.WriteUInt32LittleEndian(realBuf.AsSpan(4, 4), 255);
        BinaryPrimitives.WriteUInt32LittleEndian(realBuf.AsSpan(8, 4), (uint)name.Length);
        name.CopyTo(realBuf, 12);
        return realBuf;
    }

    private static byte[] BuildFsSizeInfo()
    {
        var buf = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), 1_000_000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), 500_000);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 1);   // SectorsPerAllocationUnit
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 2048); // BytesPerSector
        return buf;
    }

    private static byte[] BuildFsDeviceInfo()
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 7); // FILE_DEVICE_DISK
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 0);
        return buf;
    }

    private byte[] BuildFsVolumeInfo()
    {
        var name = Encoding.Unicode.GetBytes(_options.ServerName);
        var buf = new byte[18 + name.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), DateTimeOffset.UtcNow.ToFileTime()); // VolumeCreationTime
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 0x12345678);                        // VolumeSerialNumber
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), (uint)name.Length);                // VolumeLabelLength
        buf[16] = 0;                                                                                    // SupportsObjects
        buf[17] = 0;                                                                                    // Reserved
        name.CopyTo(buf, 18);
        return buf;
    }

    // ---------------------------------------------------------------------
    // QUERY_DIRECTORY - paginated enumeration.
    // ---------------------------------------------------------------------

    private async Task HandleQueryDirectoryAsync(NetworkStream stream, Smb2Header request, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var span = body.Span;
        // StructureSize(2) FileInfoClass(1) Flags(1) FileIndex(4) FileId{P(8) V(8)} FileNameOffset(2) FileNameLength(2) OutputBufferLength(4) Buffer(1)
        var infoClass = span[2];
        var flags = span[3];
        var volatileId = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8));
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(24, 2));
        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(26, 2));
        var outputBufferLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4));

        var pattern = fileNameLength == 0 ? "*"
            : Encoding.Unicode.GetString(span.Slice(fileNameOffset - Smb2Header.HeaderLength, fileNameLength));

        if (!_handles.TryGetValue(volatileId, out var handle) || !handle.IsDirectory)
        {
            await SendErrorAsync(stream, request, NtStatus.NotADirectory, ct).ConfigureAwait(false);
            return;
        }
        if (!_trees.TryGetValue(request.TreeId, out var share))
        {
            await SendErrorAsync(stream, request, NtStatus.NetworkNameDeleted, ct).ConfigureAwait(false);
            return;
        }

        // SMB2_RESTART_SCANS = 0x01, SMB2_REOPEN = 0x10
        var restart = (flags & 0x01) != 0 || (flags & 0x10) != 0;
        if (handle.Listing is null || restart || pattern != handle.LastListingPattern)
        {
            handle.Listing = EnumerateDirectory(share, handle.Path, pattern);
            handle.ListingPosition = 0;
            handle.LastListingPattern = pattern;
        }

        var entries = new List<byte[]>();
        var totalSize = 0;
        var alignment = 8;

        while (handle.ListingPosition < handle.Listing.Count)
        {
            var entry = handle.Listing[handle.ListingPosition];
            var encoded = EncodeDirectoryEntry(entry, infoClass);
            var padded = (encoded.Length + (alignment - 1)) & ~(alignment - 1);
            if (totalSize + padded > outputBufferLength && entries.Count > 0)
            {
                break;
            }
            entries.Add(encoded);
            totalSize += padded;
            handle.ListingPosition++;
        }

        if (entries.Count == 0)
        {
            await SendErrorAsync(stream, request, NtStatus.NoMoreFiles, ct).ConfigureAwait(false);
            return;
        }

        // Patch NextEntryOffset on each entry except the last.
        var assembled = new byte[totalSize];
        var pos = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var paddedLen = (entry.Length + (alignment - 1)) & ~(alignment - 1);
            var next = (i == entries.Count - 1) ? 0u : (uint)paddedLen;
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0, 4), next);
            entry.CopyTo(assembled, pos);
            pos += paddedLen;
        }

        const int respHeaderLen = 8;
        var resp = new byte[respHeaderLen + assembled.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(2, 2), (ushort)(Smb2Header.HeaderLength + respHeaderLen));
        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(4, 4), (uint)assembled.Length);
        assembled.CopyTo(resp, respHeaderLen);

        await SendAsync(stream, request.BuildResponse(), resp, ct).ConfigureAwait(false);
    }

    private static List<SmbDirectoryEntry> EnumerateDirectory(SmbShare share, string path, string pattern)
    {
        var list = new List<SmbDirectoryEntry>();
        var fs = share.FileSystem;
        var lookupPath = string.IsNullOrEmpty(path) ? "\\" : path;

        try
        {
            foreach (var dir in fs.GetDirectories(lookupPath))
            {
                if (!MatchesPattern(System.IO.Path.GetFileName(dir.TrimEnd('\\', '/')), pattern)) continue;
                DateTimeOffset created = DateTimeOffset.UtcNow;
                DateTimeOffset modified = DateTimeOffset.UtcNow;
                try { modified = new DateTimeOffset(fs.GetLastWriteTimeUtc(dir), TimeSpan.Zero); } catch { }
                try { created = new DateTimeOffset(fs.GetCreationTimeUtc(dir), TimeSpan.Zero); } catch { created = modified; }
                list.Add(new SmbDirectoryEntry(
                    System.IO.Path.GetFileName(dir.TrimEnd('\\', '/')),
                    IsDirectory: true,
                    Length: 0,
                    Created: created,
                    Modified: modified,
                    FileAttributes: FileAttributes.Directory | FileAttributes.ReadOnly));
            }
            foreach (var file in fs.GetFiles(lookupPath))
            {
                if (!MatchesPattern(System.IO.Path.GetFileName(file), pattern)) continue;
                long length = 0;
                DateTimeOffset created = DateTimeOffset.UtcNow;
                DateTimeOffset modified = DateTimeOffset.UtcNow;
                try { length = fs.GetFileLength(file); } catch { }
                try { modified = new DateTimeOffset(fs.GetLastWriteTimeUtc(file), TimeSpan.Zero); } catch { }
                try { created = new DateTimeOffset(fs.GetCreationTimeUtc(file), TimeSpan.Zero); } catch { created = modified; }
                list.Add(new SmbDirectoryEntry(
                    System.IO.Path.GetFileName(file),
                    IsDirectory: false,
                    Length: length,
                    Created: created,
                    Modified: modified,
                    FileAttributes: FileAttributes.ReadOnly | FileAttributes.Normal));
            }
        }
        catch { }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*" || string.IsNullOrEmpty(pattern)) return true;
        // Tiny glob with * and ? only
        int ni = 0, pi = 0, starN = -1, starP = -1;
        while (ni < name.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(name[ni])))
            { ni++; pi++; }
            else if (pi < pattern.Length && pattern[pi] == '*')
            { starP = pi++; starN = ni; }
            else if (starP != -1)
            { pi = starP + 1; ni = ++starN; }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    private static byte[] EncodeDirectoryEntry(SmbDirectoryEntry entry, byte infoClass)
    {
        var nameBytes = Encoding.Unicode.GetBytes(entry.Name);
        // We always emit FileIdBothDirectoryInformation - it's a superset of
        // the other common classes and Windows clients tolerate it as long
        // as we advertise it correctly.
        return BuildFileIdBothDirInfo(entry, nameBytes);
    }

    private static byte[] BuildFileIdBothDirInfo(SmbDirectoryEntry entry, byte[] nameBytes)
    {
        // 104 bytes fixed header + filename
        var buf = new byte[104 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0); // NextEntryOffset (patched later)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 0); // FileIndex
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), entry.Created.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), entry.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), entry.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32, 8), entry.Modified.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40, 8), entry.Length);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(48, 8), entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(56, 4), entry.FileAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60, 4), (uint)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64, 4), 0); // EaSize
        buf[68] = 0;                                                     // ShortNameLength
        buf[69] = 0;                                                     // Reserved
        // bytes 70-93 ShortName (24 bytes)
        // bytes 94-95 Reserved2
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(96, 8), 0);   // FileId
        nameBytes.CopyTo(buf, 104);
        return buf;
    }

    // ---------------------------------------------------------------------
    // ECHO
    // ---------------------------------------------------------------------

    private async Task HandleEchoAsync(NetworkStream stream, Smb2Header request, CancellationToken ct)
    {
        var resp = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(0, 2), 4);
        await SendAsync(stream, request.BuildResponse(), resp, ct).ConfigureAwait(false);
    }
}
