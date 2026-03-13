using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DirectLink.Common.Protocol;

const int DefaultPort = 50000;
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : DefaultPort;
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"[Server] 信令监听 0.0.0.0:{port}（IPv4），等待客户端连接。");

var relayPort = port + 1;
var relayListener = new TcpListener(IPAddress.Any, relayPort);
relayListener.Start();
Console.WriteLine($"[Server] 中继监听 0.0.0.0:{relayPort}（IPv4），文件先缓冲再交付接收方。");

var clients = new Dictionary<string, ClientSession>(StringComparer.Ordinal);
var lockObj = new object();
var relayReceivers = new ConcurrentDictionary<string, TcpClient>(StringComparer.Ordinal);
var pendingFiles = new ConcurrentDictionary<string, Queue<string>>(StringComparer.Ordinal);
var queueLocks = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
var delivering = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

_ = Task.Run(() => RelayAcceptLoopAsync(relayListener, relayReceivers, pendingFiles, queueLocks, delivering));

const int RelayChunkSize = 16 * 1024;

while (true)
{
    var tcp = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClientAsync(tcp, clients, lockObj));
}

static async Task RelayAcceptLoopAsync(
    TcpListener relayListener,
    ConcurrentDictionary<string, TcpClient> relayReceivers,
    ConcurrentDictionary<string, Queue<string>> pendingFiles,
    ConcurrentDictionary<string, object> queueLocks,
    ConcurrentDictionary<string, bool> delivering)
{
    while (true)
    {
        var tcp = await relayListener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            var buffer = new byte[RelayChunkSize];
            try
            {
                var stream = tcp.GetStream();
                var firstLine = await ReadLineAsync(stream);
                if (string.IsNullOrWhiteSpace(firstLine))
                {
                    tcp.Close();
                    return;
                }
                var parts = firstLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";

                if (cmd == ServerCommands.RelayReg && parts.Length >= 2)
                {
                    var id = parts[1].Trim();
                    if (id.Length != 3 || !id.All(char.IsDigit))
                    {
                        tcp.Close();
                        return;
                    }
                    if (relayReceivers.TryGetValue(id, out var oldTcp))
                    {
                        try { oldTcp.Close(); } catch { }
                    }
                    relayReceivers[id] = tcp;
                    Console.WriteLine($"[Server] RELAY_REG {id}");
                    DeliverNextToReceiver(id, relayReceivers, pendingFiles, queueLocks, delivering);
                    return;
                }

                if (cmd == ServerCommands.Relay && parts.Length >= 3)
                {
                    var fromId = parts[1].Trim();
                    var toId = parts[2].Trim();
                    if (!relayReceivers.ContainsKey(toId))
                    {
                        Console.WriteLine($"[Server] RELAY {fromId} -> {toId} 失败：接收方 {toId} 未注册");
                        tcp.Close();
                        return;
                    }
                    Console.WriteLine($"[Server] RELAY {fromId} -> {toId} 开始接收并缓冲");
                    string? tempPath = null;
                    try
                    {
                        tempPath = Path.Combine(Path.GetTempPath(), "DirectLink_" + Guid.NewGuid().ToString("N") + ".tmp");
                        long total = 0;
                        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            int n;
                            while ((n = await stream.ReadAsync(buffer)) > 0)
                            {
                                await fs.WriteAsync(buffer.AsMemory(0, n));
                                total += n;
                            }
                        }
                        Console.WriteLine($"[Server] RELAY {fromId} -> {toId} 缓冲完成 {total} 字节 -> {tempPath}");

                        var qLock = queueLocks.GetOrAdd(toId, _ => new object());
                        lock (qLock)
                        {
                            var q = pendingFiles.GetOrAdd(toId, _ => new Queue<string>());
                            q.Enqueue(tempPath);
                        }
                        DeliverNextToReceiver(toId, relayReceivers, pendingFiles, queueLocks, delivering);
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"[Server] RELAY {fromId} -> {toId} 读发送方异常: {ex.Message}");
                        if (tempPath != null && File.Exists(tempPath))
                            try { File.Delete(tempPath); } catch { }
                    }
                    tcp.Close();
                    return;
                }

                tcp.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] 中继处理异常: {ex.Message}");
                try { tcp.Close(); } catch { }
            }
        });
    }
}

static void DeliverNextToReceiver(
    string toId,
    ConcurrentDictionary<string, TcpClient> relayReceivers,
    ConcurrentDictionary<string, Queue<string>> pendingFiles,
    ConcurrentDictionary<string, object> queueLocks,
    ConcurrentDictionary<string, bool> delivering)
{
    string? path = null;
    var qLock = queueLocks.GetOrAdd(toId, _ => new object());
    lock (qLock)
    {
        if (delivering.GetOrAdd(toId, _ => false))
            return;
        if (!pendingFiles.TryGetValue(toId, out var q) || q.Count == 0)
            return;
        if (!relayReceivers.TryGetValue(toId, out _))
            return;
        path = q.Dequeue();
        delivering[toId] = true;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            if (!relayReceivers.TryGetValue(toId, out var receiverTcp))
            {
                try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
                return;
            }
            var receiverStream = receiverTcp.GetStream();
            var buf = new byte[RelayChunkSize];
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: RelayChunkSize, useAsync: true))
            {
                int n;
                while ((n = await fs.ReadAsync(buf)) > 0)
                {
                    lock (receiverStream)
                    {
                        receiverStream.Write(buf, 0, n);
                        receiverStream.Flush();
                    }
                }
            }
            try { File.Delete(path); } catch { }
            Console.WriteLine($"[Server] 已交付 1 个文件给 {toId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] 交付 {toId} 异常: {ex.Message}");
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }
        finally
        {
            lock (queueLocks.GetOrAdd(toId, _ => new object()))
                delivering[toId] = false;
            DeliverNextToReceiver(toId, relayReceivers, pendingFiles, queueLocks, delivering);
        }
    });
}

static async Task<string?> ReadLineAsync(Stream stream)
{
    var sb = new StringBuilder();
    var b = new byte[1];
    while (await stream.ReadAsync(b) == 1)
    {
        if (b[0] == '\n') break;
        if (b[0] != '\r') sb.Append((char)b[0]);
    }
    return sb.ToString();
}

static async Task HandleClientAsync(TcpClient tcp, Dictionary<string, ClientSession> clients, object lockObj)
{
    var remote = (IPEndPoint?)tcp.Client.RemoteEndPoint;
    var remoteStr = remote?.ToString() ?? "?";
    var isControlConnection = false;
    string? registeredClientId = null;
    using var _ = tcp;
    try
    {
        tcp.ReceiveTimeout = 60000;
        tcp.SendTimeout = 10000;
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        var line = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(line))
        {
            await writer.WriteLineAsync($"{ServerCommands.Err} empty");
            return;
        }

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await writer.WriteLineAsync($"{ServerCommands.Err} invalid");
            return;
        }

        var cmd = parts[0].ToUpperInvariant();
        var clientId = parts[1].Trim();

        if (cmd == ServerCommands.Register)
        {
            if (!IsValidClientId(clientId))
            {
                await writer.WriteLineAsync($"{ServerCommands.Err} invalid_id");
                return;
            }
            registeredClientId = clientId;
            isControlConnection = true;
            lock (lockObj)
            {
                if (clients.TryGetValue(clientId, out var existing))
                {
                    existing.ControlWriter = writer;
                    existing.ControlRemoteEndPoint = remote;
                    existing.LastActive = DateTime.UtcNow;
                }
                else
                {
                    clients[clientId] = new ClientSession
                    {
                        ClientId = clientId,
                        ControlWriter = writer,
                        ControlRemoteEndPoint = remote,
                        LastActive = DateTime.UtcNow
                    };
                }
            }
            await writer.WriteLineAsync(ServerCommands.Ok);
            Console.WriteLine($"[Server] REGISTER {clientId} from {remoteStr}");

            // 保持连接，处理后续 QUERY 等
            while (true)
            {
                line = await reader.ReadLineAsync();
                if (line == null) break;
                parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;
                cmd = parts[0].ToUpperInvariant();

                if (cmd == ServerCommands.Ping)
                {
                    lock (lockObj)
                    {
                        if (clients.TryGetValue(clientId, out var s))
                            s.LastActive = DateTime.UtcNow;
                    }
                    await writer.WriteLineAsync(ServerCommands.Pong);
                    continue;
                }

                if (cmd == ServerCommands.Query && parts.Length >= 3)
                {
                    var selfId = parts[1];
                    var peerId = parts[2];
                    if (selfId != clientId)
                    {
                        await writer.WriteLineAsync($"{ServerCommands.Err} id_mismatch");
                        continue;
                    }
                    bool peerOnline = false;
                    lock (lockObj)
                    {
                        if (clients.TryGetValue(clientId, out var s))
                            s.LastActive = DateTime.UtcNow;
                        if (clients.TryGetValue(peerId, out var peer) && peer.ControlWriter != null)
                            peerOnline = true;
                    }
                    if (!peerOnline)
                    {
                        await writer.WriteLineAsync($"{ServerCommands.Err} peer_offline");
                        continue;
                    }
                    await writer.WriteLineAsync(ServerCommands.RelayOk);
                    continue;
                }
            }
        }

        await writer.WriteLineAsync($"{ServerCommands.Err} unknown_cmd");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Server] 处理 {remoteStr} 异常: {ex.Message}");
    }
    finally
    {
        if (isControlConnection && !string.IsNullOrEmpty(registeredClientId))
        {
            lock (lockObj)
                clients.Remove(registeredClientId);
            Console.WriteLine($"[Server] 客户端 {registeredClientId} 已断开");
        }
    }
}

static bool IsValidClientId(string id)
{
    return id.Length == 3 && id.All(char.IsDigit);
}

file class ClientSession
{
    public string ClientId { get; set; } = "";
    public StreamWriter? ControlWriter { get; set; }
    public IPEndPoint? ControlRemoteEndPoint { get; set; }
    public DateTime LastActive { get; set; }
}
