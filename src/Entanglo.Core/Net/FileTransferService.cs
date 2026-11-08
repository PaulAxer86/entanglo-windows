using System.Collections.Concurrent;
using Entanglo.Core.Logging;
using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;

namespace Entanglo.Core.Net;

public enum TransferDirection { Incoming, Outgoing }

public enum TransferStatus { Offered, Active, Completed, Failed, Cancelled }

public sealed class FileTransfer
{
    public required string Id { get; init; }
    public required TransferDirection Direction { get; init; }
    public required string PeerDeviceId { get; init; }
    public required string PeerName { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public long BytesTransferred { get; set; }
    public int TotalChunks { get; init; }
    public int ChunksDone { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Offered;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DestinationPath { get; set; }
    public string? Kind { get; init; }

    public double Progress =>
        SizeBytes > 0 ? (double)BytesTransferred / SizeBytes : 0;
}

/// <summary>
/// Chunked file send/receive between trusted peers. Splits into 2 MB raw
/// chunks (~2.7 MB base64, comfortably under MessageCodec.MaxMessageBytes)
/// and reassembles on the receiver under %LocalAppData%\Entanglo\Downloads\.
/// Mirrors the Mac FileTransferService semantics on the wire so Mac ↔ Win
/// transfers work without protocol tweaks.
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSizeBytes = 2 * 1024 * 1024;

    readonly List<FileTransfer> _transfers = new();
    readonly ConcurrentDictionary<string, FileStream> _incomingHandles = new();
    readonly ConcurrentDictionary<string, int> _incomingNextSeq = new();
    readonly object _lock = new();

    /// <summary>
    /// Called when the service wants to push a frame at a specific peer.
    /// Wired by ConnectionCoordinator so we don't circular-reference it.
    /// </summary>
    public Action<string, MessageType, byte[]>? SendToPeer { get; set; }

    /// <summary>Fired right after an incoming transfer is marked completed.</summary>
    public event Action<FileTransfer>? IncomingFileComplete;

    /// <summary>Fired when an outgoing transfer reaches a terminal state.</summary>
    public event Action<FileTransfer>? OutgoingFileTerminal;

    /// <summary>Any add / update / remove — the UI re-reads the snapshot.</summary>
    public event Action? Changed;

    public IReadOnlyList<FileTransfer> Snapshot()
    {
        lock (_lock) return _transfers.ToArray();
    }

    // ---------------------------------------------------------------- sender

    public string SendFile(string filePath, string peerDeviceId, string peerName, string? kind = null)
    {
        var transferId = Guid.NewGuid().ToString();
        var name = Path.GetFileName(filePath);
        long size = 0;
        try { size = new FileInfo(filePath).Length; }
        catch (Exception ex) { AppLog.Error($"[FileTransfer] Stat failed for {filePath}: {ex.Message}"); }
        var total = Math.Max(1, (int)Math.Ceiling(size / (double)ChunkSizeBytes));

        var t = new FileTransfer
        {
            Id = transferId,
            Direction = TransferDirection.Outgoing,
            PeerDeviceId = peerDeviceId,
            PeerName = peerName,
            FileName = name,
            SizeBytes = size,
            TotalChunks = total,
            Kind = kind,
            Status = TransferStatus.Offered,
        };
        lock (_lock) _transfers.Insert(0, t);
        Changed?.Invoke();
        AppLog.Info($"[FileTransfer] Offer {name} ({size} B, {total} chunks, kind={kind ?? "file"}) → {peerName}");

        var offer = new FileOfferPayload(transferId, name, size, total, ChunkSizeBytes, kind);
        var offerBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(offer);
        SendToPeer?.Invoke(peerDeviceId, MessageType.FileOffer, offerBytes);

        t.Status = TransferStatus.Active;
        Changed?.Invoke();
        _ = Task.Run(() => StreamChunksAsync(t, filePath));
        return transferId;
    }

    async Task StreamChunksAsync(FileTransfer t, string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[ChunkSizeBytes];
            for (int seq = 0; seq < t.TotalChunks; seq++)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSizeBytes));
                if (read <= 0 && seq < t.TotalChunks - 1)
                {
                    MarkFailure(t.Id, "Read returned empty early");
                    OutgoingFileTerminal?.Invoke(t);
                    return;
                }
                var slice = new byte[read];
                Array.Copy(buffer, slice, read);
                var chunk = new FileChunkPayload(t.Id, seq, Convert.ToBase64String(slice));
                var chunkBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(chunk);
                SendToPeer?.Invoke(t.PeerDeviceId, MessageType.FileChunk, chunkBytes);
                t.ChunksDone = seq + 1;
                t.BytesTransferred += read;
                Changed?.Invoke();
                // Tiny yield so a 1 GB push doesn't starve the socket loop.
                await Task.Delay(5);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[FileTransfer] Stream failed: {ex.Message}");
            MarkFailure(t.Id, ex.Message);
            OutgoingFileTerminal?.Invoke(t);
        }
    }

    // -------------------------------------------------------------- receiver

    public void HandleOffer(FileOfferPayload payload, string peerDeviceId, string peerName)
    {
        var dstDir = DownloadsDir();
        Directory.CreateDirectory(dstDir);
        var dst = UniqueDestination(Path.Combine(dstDir, payload.Name));
        FileStream fs;
        try
        {
            fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[FileTransfer] Cannot open {dst} for write: {ex.Message}");
            SendAck(peerDeviceId, payload.TransferId, ok: false, reason: "Write open failed");
            return;
        }
        _incomingHandles[payload.TransferId] = fs;
        _incomingNextSeq[payload.TransferId] = 0;

        var t = new FileTransfer
        {
            Id = payload.TransferId,
            Direction = TransferDirection.Incoming,
            PeerDeviceId = peerDeviceId,
            PeerName = peerName,
            FileName = payload.Name,
            SizeBytes = payload.SizeBytes,
            TotalChunks = payload.TotalChunks,
            Kind = payload.Kind,
            Status = TransferStatus.Active,
            DestinationPath = dst,
        };
        lock (_lock) _transfers.Insert(0, t);
        Changed?.Invoke();
        AppLog.Info($"[FileTransfer] Incoming {payload.Name} ({payload.SizeBytes} B, kind={payload.Kind ?? "file"}) ← {peerName} → {dst}");
    }

    public void HandleChunk(FileChunkPayload payload, string peerDeviceId)
    {
        if (!_incomingHandles.TryGetValue(payload.TransferId, out var fs) ||
            !_incomingNextSeq.TryGetValue(payload.TransferId, out var nextSeq))
        {
            AppLog.Error($"[FileTransfer] Chunk for unknown transfer {payload.TransferId}");
            return;
        }
        if (payload.Sequence != nextSeq)
        {
            AppLog.Error($"[FileTransfer] Out-of-order chunk {payload.Sequence} (expected {nextSeq}) — abort");
            CloseIncoming(payload.TransferId);
            MarkFailure(payload.TransferId, "Out-of-order chunk");
            SendAck(peerDeviceId, payload.TransferId, ok: false, reason: "Out-of-order");
            return;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload.Data); }
        catch (Exception ex)
        {
            AppLog.Error($"[FileTransfer] Base64 decode failed: {ex.Message}");
            CloseIncoming(payload.TransferId);
            MarkFailure(payload.TransferId, "Base64 decode failed");
            SendAck(peerDeviceId, payload.TransferId, ok: false, reason: "Decode failed");
            return;
        }
        try { fs.Write(bytes, 0, bytes.Length); }
        catch (Exception ex)
        {
            AppLog.Error($"[FileTransfer] Write failed: {ex.Message}");
            CloseIncoming(payload.TransferId);
            MarkFailure(payload.TransferId, "Write failed");
            return;
        }
        _incomingNextSeq[payload.TransferId] = nextSeq + 1;

        FileTransfer? t;
        lock (_lock) t = _transfers.FirstOrDefault(x => x.Id == payload.TransferId);
        if (t != null)
        {
            t.ChunksDone = nextSeq + 1;
            t.BytesTransferred += bytes.Length;
            Changed?.Invoke();
            if (t.ChunksDone >= t.TotalChunks)
            {
                CloseIncoming(payload.TransferId);
                MarkCompleted(payload.TransferId);
                SendAck(peerDeviceId, payload.TransferId, ok: true);
                IncomingFileComplete?.Invoke(t);
            }
        }
    }

    public void HandleAck(FileAckPayload payload)
    {
        if (payload.Ok) MarkCompleted(payload.TransferId);
        else            MarkFailure(payload.TransferId, payload.FailureReason ?? "Remote rejected");

        FileTransfer? t;
        lock (_lock) t = _transfers.FirstOrDefault(x => x.Id == payload.TransferId);
        if (t != null && t.Direction == TransferDirection.Outgoing)
            OutgoingFileTerminal?.Invoke(t);
    }

    public void ClearFinished()
    {
        lock (_lock)
        {
            _transfers.RemoveAll(t =>
                t.Status is TransferStatus.Completed
                          or TransferStatus.Failed
                          or TransferStatus.Cancelled);
        }
        Changed?.Invoke();
    }

    // --------------------------------------------------------------- helpers

    void CloseIncoming(string id)
    {
        if (_incomingHandles.TryRemove(id, out var fs))
        {
            try { fs.Dispose(); } catch { }
        }
        _incomingNextSeq.TryRemove(id, out _);
    }

    void MarkCompleted(string id)
    {
        lock (_lock)
        {
            var t = _transfers.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.Status = TransferStatus.Completed;
            t.FinishedAt = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke();
    }

    void MarkFailure(string id, string reason)
    {
        lock (_lock)
        {
            var t = _transfers.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.Status = TransferStatus.Failed;
            t.ErrorMessage = reason;
            t.FinishedAt = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke();
    }

    void SendAck(string peerId, string transferId, bool ok, string? reason = null)
    {
        var ack = new FileAckPayload(transferId, ok, reason);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(ack);
        SendToPeer?.Invoke(peerId, MessageType.FileAck, bytes);
    }

    public static string DownloadsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Entanglo", "Downloads");

    static string UniqueDestination(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }
}
