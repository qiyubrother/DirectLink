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
Console.WriteLine($"[Server] 中继监听 0.0.0.0:{relayPort}（IPv4），直连失败时可经此转发文件。");

var clients = new Dictionary<string, ClientSession>(StringComparer.Ordinal);
var lockObj = new object();
var relayReceivers = new ConcurrentDictionary<string, Stream>(StringComparer.Ordinal);

_ = Task.Run(() => RelayAcceptLoopAsync(relayListener, relayReceivers));

while (true)
{
    var tcp = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClientAsync(tcp, clients, lockObj));
}

static async Task RelayAcceptLoopAsync(TcpListener relayListener, ConcurrentDictionary<string, Stream> relayReceivers)
{
    const int bufSize = 64 * 1024;
    var buffer = new byte[bufSize];
    while (true)
    {
        var tcp = await relayListener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
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
                    if (relayReceivers.TryGetValue(id, out var oldStream))
                    {
                        try { oldStream.Close(); } catch { }
                    }
                    relayReceivers[id] = stream;
                    Console.WriteLine($"[Server] RELAY_REG {id}");
                    return;
                }

                if (cmd == ServerCommands.Relay && parts.Length >= 3)
                {
                    var fromId = parts[1].Trim();
                    var toId = parts[2].Trim();
                    if (!relayReceivers.TryGetValue(toId, out var receiverStream))
                    {
                        tcp.Close();
                        return;
                    }
                    Console.WriteLine($"[Server] RELAY {fromId} -> {toId}");
                    try
                    {
                        int n;
                        while ((n = await stream.ReadAsync(buffer)) > 0)
                        {
                            lock (receiverStream)
                            {
                                receiverStream.Write(buffer.AsSpan(0, n));
                                receiverStream.Flush();
                            }
                        }
                    }
                    catch (IOException)
                    {
                        relayReceivers.TryRemove(toId, out _);
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

        if (cmd == ServerCommands.PortMap)
        {
            // PORT_MAP <ClientId> [ListenPort]：
            // 当前连接的 RemoteEndPoint.Address 作为客户端对外地址，
            // 若提供 ListenPort，则使用该端口作为 P2P 端口（而不是本次连接的本地端口），
            // 这样客户端无需在本连接上绑定同一端口，避免端口复用冲突。
            if (!IsValidClientId(clientId))
            {
                await writer.WriteLineAsync($"{ServerCommands.Err} invalid_id");
                return;
            }
            int? reportedPort = null;
            if (parts.Length >= 3 && int.TryParse(parts[2], out var rp) && rp > 0 && rp <= 65535)
                reportedPort = rp;

            var p2pEndPoint = remote;
            if (p2pEndPoint != null && reportedPort != null)
                p2pEndPoint = new IPEndPoint(p2pEndPoint.Address, reportedPort.Value);

            lock (lockObj)
            {
                if (clients.TryGetValue(clientId, out var existing))
                {
                    existing.P2PEndPoint = p2pEndPoint;
                    existing.LastActive = DateTime.UtcNow;
                }
                else
                {
                    clients[clientId] = new ClientSession
                    {
                        ClientId = clientId,
                        P2PEndPoint = p2pEndPoint,
                        LastActive = DateTime.UtcNow
                    };
                }
            }
            var okLine = p2pEndPoint != null
                ? $"{ServerCommands.Ok} {p2pEndPoint.Address} {p2pEndPoint.Port}"
                : ServerCommands.Ok;
            await writer.WriteLineAsync(okLine);
            Console.WriteLine($"[Server] PORT_MAP {clientId} -> {p2pEndPoint}");
            return;
        }

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
                    IPEndPoint? peerP2P = null;
                    IPEndPoint? senderP2P = null;
                    StreamWriter? peerWriter = null;
                    lock (lockObj)
                    {
                        if (clients.TryGetValue(clientId, out var s))
                        {
                            s.LastActive = DateTime.UtcNow;
                            senderP2P = s.P2PEndPoint;
                        }
                        if (clients.TryGetValue(peerId, out var peer))
                        {
                            peerP2P = peer.P2PEndPoint;
                            peerWriter = peer.ControlWriter;
                        }
                    }
                    if (peerP2P == null || peerWriter == null)
                    {
                        await writer.WriteLineAsync($"{ServerCommands.Err} peer_offline");
                        continue;
                    }
                    if (senderP2P == null)
                    {
                        await writer.WriteLineAsync($"{ServerCommands.Err} no_p2p_port");
                        continue;
                    }
                    await writer.WriteLineAsync($"{ServerCommands.P2P} {peerP2P.Address} {peerP2P.Port}");
                    try
                    {
                        await peerWriter.WriteLineAsync($"{ServerCommands.Incoming} {clientId} {senderP2P.Address} {senderP2P.Port}");
                        peerWriter.Flush();
                    }
                    catch { /* 对端可能已断开 */ }
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
    public IPEndPoint? P2PEndPoint { get; set; }
    public DateTime LastActive { get; set; }
}
