using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DirectLink.Common.Protocol;

namespace DirectLink.Client.Services;

/// <summary>
/// 与中继服务端的连接：注册、上报 P2P 端口、查询对端地址，并持续读取 INCOMING 等。
/// 支持 IPv6 优先：解析与连接时先尝试 IPv6，再回退 IPv4。
/// </summary>
public class RelayService : IAsyncDisposable
{
    /// <summary>解析主机并返回端点列表，IPv6 在前、IPv4 在后，便于优先使用 IPv6。</summary>
    private static IPEndPoint[] GetEndpointsPreferIPv6(string host, int port)
    {
        if (IPAddress.TryParse(host, out var single))
            return new[] { new IPEndPoint(single, port) };
        var addrs = Dns.GetHostAddresses(host);
        var v6 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv4MappedToIPv6).ToArray();
        var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        return v6.Select(a => new IPEndPoint(a, port)).Concat(v4.Select(a => new IPEndPoint(a, port))).ToArray();
    }

    /// <summary>优先 IPv6 连接至 host:port，返回已连接的 TcpClient。关闭时使用 Linger(0) 避免 TIME_WAIT，便于立即重连。</summary>
    private static async Task<TcpClient> ConnectPreferIPv6Async(string host, int port, CancellationToken ct = default)
    {
        var endpoints = GetEndpointsPreferIPv6(host, port);
        if (endpoints.Length == 0)
            throw new InvalidOperationException($"无法解析主机: {host}");
        Exception? lastEx = null;
        foreach (var ep in endpoints)
        {
            var client = new TcpClient(ep.AddressFamily);
            try
            {
                await client.ConnectAsync(ep, ct);
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0)); } catch { }
                return client;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                try { client.Close(); } catch { }
            }
        }
        throw lastEx ?? new InvalidOperationException("连接失败");
    }

    /// <summary>从本地端口 localPort 连至 host:port，优先 IPv6，便于服务端记录本机 P2P 端点。</summary>
    private static async Task<TcpClient> ConnectPreferIPv6FromLocalPortAsync(string host, int port, int localPort, CancellationToken ct = default)
    {
        var endpoints = GetEndpointsPreferIPv6(host, port);
        if (endpoints.Length == 0)
            throw new InvalidOperationException($"无法解析主机: {host}");
        Exception? lastEx = null;
        foreach (var ep in endpoints)
        {
            var client = new TcpClient(ep.AddressFamily);
            try
            {
                var local = ep.AddressFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, localPort)
                    : new IPEndPoint(IPAddress.Any, localPort);
                client.Client.Bind(local);
                await client.ConnectAsync(ep, ct);
                return client;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                try { client.Close(); } catch { }
            }
        }
        throw lastEx ?? new InvalidOperationException("PORT_MAP 连接失败");
    }

    private TcpClient? _controlClient;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly string _clientId;
    private CancellationTokenSource? _pingCts;
    private TaskCompletionSource<string?>? _pendingQueryResponse;
    private readonly object _readLock = new();

    private TcpClient? _relayReceiverClient;
    private Stream? _relayReceiverStream;

    /// <summary>收到 INCOMING 时：senderId, peerHost, peerPort（接收方应主动连过去以配合打洞）</summary>
    public event Action<string, string, int>? IncomingSendRequest;

    public RelayService(string serverHost, int serverPort, string clientId)
    {
        _serverHost = serverHost;
        _serverPort = serverPort;
        _clientId = clientId;
    }

    /// <summary>
    /// 建立控制连接并注册；之后会启动读循环，可调用 QueryPeer 与接收 INCOMING。
    /// </summary>
    public async Task ConnectAndRegisterAsync(CancellationToken ct = default)
    {
        _controlClient = await ConnectPreferIPv6Async(_serverHost, _serverPort, ct);
        var stream = _controlClient.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        await _writer.WriteLineAsync($"REGISTER {_clientId}");
        var response = await _reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(response) || !response.StartsWith(ServerCommands.Ok, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"注册失败: {response}");
        StartPing();
        _ = RunReadLoopAsync();
    }

    private async Task RunReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var reader = _reader;
                if (reader == null) break;
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                var trimmed = line.Trim();
                TaskCompletionSource<string?>? tcs;
                lock (_readLock)
                {
                    tcs = _pendingQueryResponse;
                    _pendingQueryResponse = null;
                }
                if (tcs != null)
                {
                    try { tcs.TrySetResult(trimmed); } catch { }
                    continue;
                }
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;
                var cmd = parts[0].ToUpperInvariant();
                if (cmd == ServerCommands.Pong)
                    continue;
                if (cmd == ServerCommands.Incoming && parts.Length >= 4 && int.TryParse(parts[3], out var port))
                {
                    var senderId = parts[1];
                    var host = parts[2];
                    IncomingSendRequest?.Invoke(senderId, host, port);
                }
            }
        }
        catch (Exception) { /* 连接断开 */ }
    }

    /// <summary>
    /// 上报本机用于 P2P 监听的端口给服务端。
    /// 不再绑定本地端口，只通过命令参数告知监听端口，避免与监听 Socket 发生端口冲突。
    /// </summary>
    public static async Task ReportP2PPortAsync(string serverHost, int serverPort, string clientId, int listenPort, CancellationToken ct = default)
    {
        var client = await ConnectPreferIPv6Async(serverHost, serverPort, ct);
        try
        {
            var stream = client.GetStream();
            using var w = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var r = new StreamReader(stream, Encoding.UTF8);
            await w.WriteLineAsync($"PORT_MAP {clientId} {listenPort}");
            var line = await r.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith(ServerCommands.Ok, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"PORT_MAP 失败: {line}");
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>
    /// 连接中继端口并注册为接收方，返回用于接收经中继转发文件的流。调用方负责在该流上循环调用 ReceiveFromStreamAsync 并处理断开。
    /// </summary>
    public async Task<Stream?> OpenRelayReceiverAsync(CancellationToken ct = default)
    {
        try
        {
            try { _relayReceiverClient?.Close(); } catch { }
            _relayReceiverClient = await ConnectPreferIPv6Async(_serverHost, _serverPort + 1, ct);
            var stream = _relayReceiverClient.GetStream();
            var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await w.WriteLineAsync($"{ServerCommands.RelayReg} {_clientId}");
            await w.FlushAsync(ct);
            _relayReceiverStream = stream;
            return stream;
        }
        catch
        {
            _relayReceiverClient = null;
            _relayReceiverStream = null;
            return null;
        }
    }

    /// <summary>
    /// 经服务器中继发送文件到对端；直连失败时可用。
    /// </summary>
    public static async Task<(long bytesSent, bool success)> SendFileViaRelayAsync(
        string serverHost,
        int serverPort,
        string fromClientId,
        string toPeerId,
        string filePath,
        IProgress<TransferProgress>? progress,
        CancellationToken ct = default)
    {
        var client = await ConnectPreferIPv6Async(serverHost, serverPort + 1, ct);
        try
        {
            var stream = client.GetStream();
            var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await w.WriteLineAsync($"{ServerCommands.Relay} {fromClientId} {toPeerId}");
            await w.FlushAsync(ct);
            return await P2PTransferService.SendFileToStreamAsync(stream, filePath, 0, progress);
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>
    /// 检查对端是否在线（已注册且可经中继发送）。仅走中转时使用。
    /// </summary>
    public async Task<bool> IsPeerOnlineAsync(string peerId, CancellationToken ct = default)
    {
        if (_writer == null || _reader == null)
            return false;
        var tcs = new TaskCompletionSource<string?>();
        lock (_readLock)
        {
            if (_pendingQueryResponse != null)
                _pendingQueryResponse.TrySetCanceled();
            _pendingQueryResponse = tcs;
        }
        await _writer.WriteLineAsync($"QUERY {_clientId} {peerId}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var line = await tcs.Task.WaitAsync(cts.Token);
            if (string.IsNullOrEmpty(line)) return false;
            var t = line.Trim();
            if (t.StartsWith(ServerCommands.Err, StringComparison.OrdinalIgnoreCase)) return false;
            if (t.StartsWith(ServerCommands.RelayOk, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_readLock) { _pendingQueryResponse = null; }
            return false;
        }
    }

    /// <summary>
    /// 查询对端 P2P 地址；成功返回 (peerHost, peerPort)。（已废弃，仅中转时用 IsPeerOnlineAsync）
    /// </summary>
    public async Task<(string? peerHost, int? peerPort)> QueryPeerAsync(string peerId, CancellationToken ct = default)
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("未连接服务端");
        var tcs = new TaskCompletionSource<string?>();
        lock (_readLock)
        {
            if (_pendingQueryResponse != null)
                _pendingQueryResponse.TrySetCanceled();
            _pendingQueryResponse = tcs;
        }
        await _writer.WriteLineAsync($"QUERY {_clientId} {peerId}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var line = await tcs.Task.WaitAsync(cts.Token);
            if (string.IsNullOrEmpty(line) || line.StartsWith(ServerCommands.Err, StringComparison.OrdinalIgnoreCase))
                return (null, null);
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && string.Equals(parts[0], ServerCommands.P2P, StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out var port))
                return (parts[1], port);
            return (null, null);
        }
        catch (OperationCanceledException)
        {
            lock (_readLock) { _pendingQueryResponse = null; }
            return (null, null);
        }
    }

    private void StartPing()
    {
        _pingCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_pingCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), _pingCts.Token).ConfigureAwait(false);
                try
                {
                    if (_writer != null)
                        await _writer.WriteLineAsync(ServerCommands.Ping).ConfigureAwait(false);
                }
                catch { break; }
            }
        }, _pingCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _pingCts?.Cancel();
        lock (_readLock) { _pendingQueryResponse?.TrySetCanceled(); }
        if (_relayReceiverClient != null)
        {
            try { _relayReceiverClient.Close(); } catch { }
            _relayReceiverClient = null;
            _relayReceiverStream = null;
        }
        if (_controlClient != null)
        {
            try { _controlClient.Close(); } catch { }
            _controlClient = null;
        }
        _writer = null;
        _reader = null;
        await Task.CompletedTask;
    }
}
