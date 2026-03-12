using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DirectLink.Common;
using DirectLink.Common.Dto;
using DirectLink.Common.Protocol;

namespace DirectLink.Client.Maui.Services;

public class P2PTransferService
{
    public const int DefaultBlockSize = P2PCommands.DefaultBlockSize;
    public const int P2PListenPortBase = 50001;

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
            if (b[0] == (byte)'\n') break;
            lineBuffer.Add(b[0]);
        }
        var metaLine = Encoding.UTF8.GetString(lineBuffer.ToArray()).TrimStart('\uFEFF'); // 去除可能的 BOM
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

        string saveDir = string.IsNullOrWhiteSpace(saveDirectory) ? Path.GetTempPath() : saveDirectory;
        if (!Directory.Exists(saveDir))
        {
            Directory.CreateDirectory(saveDir);
            TransferFileLogger.Write("P2P-RECV", $"已创建保存目录: {saveDir}");
        }

        var safeName = Path.GetFileName(meta.FileName);
        if (string.IsNullOrEmpty(safeName)) safeName = "received.dat";
        var tmpPath = Path.Combine(saveDir, safeName + ".tmp");
        var metaPath = Path.Combine(saveDir, safeName + ".tmp.meta");
        var targetPath = Path.Combine(saveDir, safeName);

        long resumeOffset = meta.ResumeOffset;
        if (resumeOffset > 0 && File.Exists(metaPath))
        {
            try
            {
                var metaContent = await File.ReadAllTextAsync(metaPath, ct);
                if (long.TryParse(metaContent.Trim(), out var saved))
                    resumeOffset = Math.Min(saved, meta.FileSize);
            }
            catch { /* 忽略旧 meta 读失败 */ }
        }

        try
        {
            await File.WriteAllTextAsync(metaPath, resumeOffset.ToString(), ct);
        }
        catch (Exception)
        {
            await DrainStreamAsync(stream, 0, meta.FileSize, ct);
            throw;
        }
        FileStream? fs;
        try
        {
            fs = new FileStream(tmpPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        }
        catch (Exception)
        {
            await DrainStreamAsync(stream, 0, meta.FileSize, ct);
            throw;
        }
        await using (fs)
        {
        if (fs.Length < resumeOffset) fs.SetLength(resumeOffset);
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
            if (read == 0) break;
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
            if (File.Exists(targetPath)) File.Delete(targetPath);
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
    }

    /// <summary>读取并丢弃指定字节数，便于发送端正常结束写入（接收端出错时避免 RST）</summary>
    private static async Task DrainStreamAsync(Stream stream, long alreadyRead, long totalSize, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(DefaultBlockSize, (int)Math.Min(totalSize - alreadyRead, int.MaxValue))];
        if (buffer.Length == 0) return;
        long drained = alreadyRead;
        while (drained < totalSize)
        {
            var toRead = (int)Math.Min(totalSize - drained, buffer.Length);
            var r = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (r == 0) break;
            drained += r;
        }
    }

    public static async Task<(long bytesSent, bool success)> SendFileToStreamAsync(
        Stream stream,
        string filePath,
        long resumeOffset,
        IProgress<TransferProgress>? progress,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return (0, false);
        var fileSize = fileInfo.Length;
        if (resumeOffset >= fileSize) return (fileSize, true);

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
        TransferFileLogger.Write("P2P-SEND", $"准备发送文件: Path='{filePath}', Size={fileSize}, ResumeOffset={resumeOffset}");
        TransferFileLogger.Write("P2P-SEND", $"META_LINE='{metaLine.TrimEnd()}'");
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
        TransferFileLogger.Write("P2P-SEND", $"发送结束: Path='{filePath}', Bytes={totalSent}, Success={totalSent == fileSize}");
        return (totalSent, totalSent == fileSize);
    }
}

public record TransferProgress(long Current, long Total, double BytesPerSecond);
