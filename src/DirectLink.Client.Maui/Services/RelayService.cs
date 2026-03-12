using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DirectLink.Common.Protocol;

namespace DirectLink.Client.Maui.Services;

public class RelayService : IAsyncDisposable
{
    private static IPEndPoint[] GetEndpointsPreferIPv6(string host, int port)
    {
        if (IPAddress.TryParse(host, out var single))
            return new[] { new IPEndPoint(single, port) };
        var addrs = Dns.GetHostAddresses(host);
        var v6 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv4MappedToIPv6).ToArray();
        var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        return v6.Select(a => new IPEndPoint(a, port)).Concat(v4.Select(a => new IPEndPoint(a, port))).ToArray();
    }

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

    public event Action<string, string, int>? IncomingSendRequest;

    public RelayService(string serverHost, int serverPort, string clientId)
    {
        _serverHost = serverHost;
        _serverPort = serverPort;
        _clientId = clientId;
    }

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
        if (_reader == null) return;
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync();
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
                    IncomingSendRequest?.Invoke(parts[1], parts[2], port);
            }
        }
        catch (Exception) { }
    }

    public static async Task ReportP2PPortAsync(string serverHost, int serverPort, string clientId, int localPort, CancellationToken ct = default)
    {
        var client = await ConnectPreferIPv6FromLocalPortAsync(serverHost, serverPort, localPort, ct);
        try
        {
            var stream = client.GetStream();
            using var w = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var r = new StreamReader(stream, Encoding.UTF8);
            await w.WriteLineAsync($"PORT_MAP {clientId}");
            var line = await r.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith(ServerCommands.Ok, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"PORT_MAP 失败: {line}");
        }
        finally
        {
            client.Close();
        }
    }

    public async Task<(string? peerHost, int? peerPort)> QueryPeerAsync(string peerId, CancellationToken ct = default)
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("未连接服务端");
        var tcs = new TaskCompletionSource<string?>();
        lock (_readLock)
        {
            _pendingQueryResponse?.TrySetCanceled();
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
        try { _controlClient?.Close(); } catch { }
        _controlClient = null;
        _writer = null;
        _reader = null;
        await Task.CompletedTask;
    }
}
