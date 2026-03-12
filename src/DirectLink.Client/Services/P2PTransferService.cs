using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DirectLink.Common;
using DirectLink.Common.Dto;
using DirectLink.Common.Protocol;
using DirectLink.Client.Services;

namespace DirectLink.Client.Services;

/// <summary>
/// P2P 直连文件传输：发送/接收，支持断点续传与哈希校验。
/// </summary>
public class P2PTransferService
{
    public const int DefaultBlockSize = P2PCommands.DefaultBlockSize;
    public const int P2PListenPortBase = 50001;

    /// <summary>
    /// 作为接收方：在 localPort 上监听，接受一个连接并接收文件到 saveDirectory；返回最终文件路径；支持断点续传。
    /// </summary>
    public static async Task<(string? savedPath, long bytesReceived, bool hashOk)> ReceiveFileAsync(
        int localPort,
        string saveDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken ct = default)
    {
        using var listener = new TcpListener(IPAddress.Any, localPort);
        listener.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        await using var stream = client.GetStream();
        return await ReceiveFromStreamAsync(stream, saveDirectory, progress, ct);
    }

    /// <summary>
    /// 从已建立的流中接收文件（先读 FILE_META 行 + JSON，再读二进制）。
    /// </summary>
    public static async Task<(string? savedPath, long bytesReceived, bool hashOk)> ReceiveFromStreamAsync(
        Stream stream,
        string saveDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken ct = default)
    {
        TransferFileLogger.Write("P2P-RECV", $"开始从流接收文件，saveDirectory='{saveDirectory}'");
        var lineBuffer = new List<byte>();
        var b = new byte[1];
        while (await stream.ReadAsync(b, ct) == 1)
        {
            if (b[0] == (byte)'\n')
                break;
            lineBuffer.Add(b[0]);
        }
        // 移除可能存在的 UTF-8 BOM，避免首行变成 "\uFEFFFILE_META ..."
        var metaLine = Encoding.UTF8.GetString(lineBuffer.ToArray()).TrimStart('\uFEFF');
        TransferFileLogger.Write("P2P-RECV", $"META_LINE='{metaLine}'");
        if (string.IsNullOrEmpty(metaLine) || !metaLine.StartsWith(P2PCommands.FileMeta, StringComparison.OrdinalIgnoreCase))
        {
            TransferFileLogger.Write("P2P-RECV", "首行不是 FILE_META，放弃本次接收。");
            return (null, 0, false);
        }
        var json = metaLine.Substring(P2PCommands.FileMeta.Length).Trim();
        TransferFileLogger.Write("P2P-RECV", $"META_JSON='{json}'");
        FileTransferMeta? meta = null;
        try
        {
            meta = JsonSerializer.Deserialize<FileTransferMeta>(json);
        }
        catch (Exception ex)
        {
            TransferFileLogger.Write("P2P-RECV", $"META JSON 解析异常: {ex.Message}");
            return (null, 0, false);
        }
        if (meta == null)
        {
            TransferFileLogger.Write("P2P-RECV", "META JSON 解析结果为空，放弃本次接收。");
            return (null, 0, false);
        }

        if (!Directory.Exists(saveDirectory))
        {
            Directory.CreateDirectory(saveDirectory);
            TransferFileLogger.Write("P2P-RECV", $"已创建保存目录: {saveDirectory}");
        }

        var safeName = Path.GetFileName(meta.FileName);
        if (string.IsNullOrEmpty(safeName))
            safeName = "received.dat";
        var tmpPath = Path.Combine(saveDirectory, safeName + ".tmp");
        var metaPath = Path.Combine(saveDirectory, safeName + ".tmp.meta");
        var targetPath = Path.Combine(saveDirectory, safeName);

        long resumeOffset = meta.ResumeOffset;
        if (resumeOffset > 0 && File.Exists(metaPath))
        {
            var metaContent = await File.ReadAllTextAsync(metaPath, ct);
            if (long.TryParse(metaContent.Trim(), out var saved))
                resumeOffset = Math.Min(saved, meta.FileSize);
        }

        await File.WriteAllTextAsync(metaPath, resumeOffset.ToString(), ct);
        await using var fs = new FileStream(tmpPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (fs.Length < resumeOffset)
            fs.SetLength(resumeOffset);
        fs.Seek(resumeOffset, SeekOrigin.Begin);

        var buffer = new byte[DefaultBlockSize];
        long totalReceived = resumeOffset;
        var lastReport = DateTime.UtcNow;
        long lastReportBytes = totalReceived;

        TransferFileLogger.Write("P2P-RECV", $"开始接收数据: FileName='{safeName}', FileSize={meta.FileSize}, ResumeOffset={resumeOffset}, Target='{targetPath}'");

        while (totalReceived < meta.FileSize)
        {
            var toRead = (int)Math.Min(meta.FileSize - totalReceived, buffer.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0)
                break;
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            totalReceived += read;
            await File.WriteAllTextAsync(metaPath, totalReceived.ToString(), ct);

            var now = DateTime.UtcNow;
            if (progress != null && (now - lastReport).TotalMilliseconds >= 200)
            {
                var speed = (totalReceived - lastReportBytes) / ((now - lastReport).TotalSeconds + 1e-6);
                progress.Report(new TransferProgress(totalReceived, meta.FileSize, speed));
                lastReport = now;
                lastReportBytes = totalReceived;
            }
        }

        fs.Close();
        if (totalReceived >= meta.FileSize)
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tmpPath, targetPath);
            try { File.Delete(metaPath); } catch { }
        }

        bool hashOk = false;
        if (totalReceived == meta.FileSize && !string.IsNullOrEmpty(meta.FileHash))
        {
            var computed = HashUtil.ComputeSha256Sync(targetPath);
            hashOk = string.Equals(computed, meta.FileHash, StringComparison.OrdinalIgnoreCase);
        }
        else if (totalReceived == meta.FileSize)
            hashOk = true;

        progress?.Report(new TransferProgress(meta.FileSize, meta.FileSize, 0));
        TransferFileLogger.Write("P2P-RECV", $"接收结束: SavedPath='{(totalReceived == meta.FileSize ? targetPath : null)}', Bytes={totalReceived}, HashOk={hashOk}");
        return (totalReceived == meta.FileSize ? targetPath : null, totalReceived, hashOk);
    }

    /// <summary>
    /// 作为发送方：连接到 peerHost:peerPort，发送文件（支持从 resumeOffset 续传）。
    /// </summary>
    public static async Task<(long bytesSent, bool success)> SendFileAsync(
        string filePath,
        string peerHost,
        int peerPort,
        long resumeOffset,
        IProgress<TransferProgress>? progress,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            return (0, false);
        var fileSize = fileInfo.Length;
        if (resumeOffset >= fileSize)
            return (fileSize, true);

        string? fileHash = null;
        try
        {
            fileHash = HashUtil.ComputeSha256Sync(filePath);
        }
        catch { /* 大文件可先不预计算，接收端校验 */ }

        var meta = new FileTransferMeta
        {
            FileName = fileInfo.Name,
            FileSize = fileSize,
            FileHash = fileHash,
            ResumeOffset = resumeOffset
        };
        var json = JsonSerializer.Serialize(meta);
        var metaLine = P2PCommands.FileMeta + " " + json + "\n";
        TransferFileLogger.Write("P2P-SEND", $"准备发送文件: Path='{filePath}', Size={fileSize}, ResumeOffset={resumeOffset}, Peer={peerHost}:{peerPort}");
        TransferFileLogger.Write("P2P-SEND", $"META_LINE='{metaLine.TrimEnd()}'");

        using var client = new TcpClient();
        TransferFileLogger.Write("P2P-SEND", "开始连接对端...");
        await client.ConnectAsync(peerHost, peerPort, ct);
        TransferFileLogger.Write("P2P-SEND", "对端 TCP 连接已建立。");
        await using var stream = client.GetStream();
        // 发送端也使用无 BOM UTF-8，避免在首行前写入 BOM
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteAsync(metaLine);
        await writer.FlushAsync(ct);

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(resumeOffset, SeekOrigin.Begin);
        var buffer = new byte[DefaultBlockSize];
        long totalSent = resumeOffset;
        var lastReport = DateTime.UtcNow;
        long lastReportBytes = totalSent;

        while (totalSent < fileSize)
        {
            var toSend = (int)Math.Min(fileSize - totalSent, buffer.Length);
            var read = await fs.ReadAsync(buffer.AsMemory(0, toSend), ct);
            if (read == 0)
                break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalSent += read;

            var now = DateTime.UtcNow;
            if (progress != null && (now - lastReport).TotalMilliseconds >= 200)
            {
                var speed = (totalSent - lastReportBytes) / ((now - lastReport).TotalSeconds + 1e-6);
                progress.Report(new TransferProgress(totalSent, fileSize, speed));
                lastReport = now;
                lastReportBytes = totalSent;
            }
        }

        progress?.Report(new TransferProgress(fileSize, fileSize, 0));
        TransferFileLogger.Write("P2P-SEND", $"发送结束: Path='{filePath}', Bytes={totalSent}, Success={totalSent == fileSize}");
        return (totalSent, totalSent == fileSize);
    }

    /// <summary>
    /// 向已有流发送文件（用于“对端连上我们”时由我方主动写流）。
    /// 调用方负责 stream 的生命周期，本方法不 dispose。
    /// </summary>
    public static async Task<(long bytesSent, bool success)> SendFileToStreamAsync(
        Stream stream,
        string filePath,
        long resumeOffset,
        IProgress<TransferProgress>? progress,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            return (0, false);
        var fileSize = fileInfo.Length;
        if (resumeOffset >= fileSize)
            return (fileSize, true);

        string? fileHash = null;
        try { fileHash = HashUtil.ComputeSha256Sync(filePath); } catch { }
        var meta = new FileTransferMeta
        {
            FileName = fileInfo.Name,
            FileSize = fileSize,
            FileHash = fileHash,
            ResumeOffset = resumeOffset
        };
        var json = JsonSerializer.Serialize(meta);
        var metaLine = P2PCommands.FileMeta + " " + json + "\n";
        TransferFileLogger.Write("P2P-SEND", $"(已有流) 准备发送文件: Path='{filePath}', Size={fileSize}, ResumeOffset={resumeOffset}");
        TransferFileLogger.Write("P2P-SEND", $"(已有流) META_LINE='{metaLine.TrimEnd()}'");
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteAsync(metaLine);
        await writer.FlushAsync(ct);

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(resumeOffset, SeekOrigin.Begin);
        var buffer = new byte[DefaultBlockSize];
        long totalSent = resumeOffset;
        var lastReport = DateTime.UtcNow;
        long lastReportBytes = totalSent;

        while (totalSent < fileSize)
        {
            var toSend = (int)Math.Min(fileSize - totalSent, buffer.Length);
            var read = await fs.ReadAsync(buffer.AsMemory(0, toSend), ct);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalSent += read;
            var now = DateTime.UtcNow;
            if (progress != null && (now - lastReport).TotalMilliseconds >= 200)
            {
                var speed = (totalSent - lastReportBytes) / ((now - lastReport).TotalSeconds + 1e-6);
                progress.Report(new TransferProgress(totalSent, fileSize, speed));
                lastReport = now;
                lastReportBytes = totalSent;
            }
        }
        progress?.Report(new TransferProgress(fileSize, fileSize, 0));
        TransferFileLogger.Write("P2P-SEND", $"(已有流) 发送结束: Path='{filePath}', Bytes={totalSent}, Success={totalSent == fileSize}");
        return (totalSent, totalSent == fileSize);
    }
}

public record TransferProgress(long Current, long Total, double BytesPerSecond);
