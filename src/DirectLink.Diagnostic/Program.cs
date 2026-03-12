using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

// 用法: DirectLink.Diagnostic [服务端地址] [对端ID可选]
// 示例: DirectLink.Diagnostic 127.0.0.1:50000
//       DirectLink.Diagnostic 192.168.1.100:50000 77260
var serverHost = "127.0.0.1";
var serverPort = 50000;
var peerId = "";
if (args.Length >= 1)
{
    var parts = args[0].Split(':', StringSplitOptions.RemoveEmptyEntries);
    serverHost = parts.Length > 0 ? parts[0].Trim() : serverHost;
    serverPort = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : serverPort;
}
if (args.Length >= 2)
    peerId = args[1].Trim();

var diagId = "99999";
var p2pPort = 50199;
var log = new List<string>();
var sw = new StringWriter();
void L(string line)
{
    log.Add(line);
    sw.WriteLine(line);
    Console.WriteLine(line);
}

var outPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    $"DirectLinkDiagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

L("========== DirectLink 直连诊断 ==========");
L($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
L($"OS: {Environment.OSVersion}");
L($".NET: {Environment.Version}");
L("");

try
{
    L("--- 本机网络 ---");
    var hostName = Dns.GetHostName();
    L($"主机名: {hostName}");
    foreach (var ip in Dns.GetHostAddresses(hostName))
        L($"  本机IP: {ip}");
    L("");

    L("--- 步骤1: 连接服务端并 REGISTER ---");
    using (var ctrl = new TcpClient())
    {
        ctrl.Connect(serverHost, serverPort);
        var remote = (IPEndPoint?)ctrl.Client.RemoteEndPoint;
        L($"  已连接: {remote}");
        var stream = ctrl.GetStream();
        using var r = new StreamReader(stream, Encoding.UTF8);
        using var w = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        w.WriteLine($"REGISTER {diagId}");
        var line = r.ReadLine();
        L($"  发送 REGISTER {diagId} -> 收到: {line ?? "(null)"}");
        if (string.IsNullOrEmpty(line) || !line.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            L("  [失败] REGISTER 未返回 OK");
        }
    }
    L("");

    L("--- 步骤2: PORT_MAP（从本地端口连服务端，让服务端记录本机 P2P 地址）---");
    string? serverSeesIp = null;
    int serverSeesPort = 0;
    using (var mapClient = new TcpClient())
    {
        mapClient.Client.Bind(new IPEndPoint(IPAddress.Any, p2pPort));
        L($"  已绑定本地端口: {p2pPort}");
        mapClient.Connect(serverHost, serverPort);
        var fromUs = (IPEndPoint?)mapClient.Client.LocalEndPoint;
        var serverRemote = (IPEndPoint?)mapClient.Client.RemoteEndPoint;
        L($"  本机端点: {fromUs}, 服务端端点: {serverRemote}");
        var stream = mapClient.GetStream();
        using var r = new StreamReader(stream, Encoding.UTF8);
        using var w = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        w.WriteLine($"PORT_MAP {diagId}");
        var line = r.ReadLine();
        L($"  发送 PORT_MAP {diagId} -> 收到: {line ?? "(null)"}");
        var parts = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("OK", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out var port))
        {
            serverSeesIp = parts[1];
            serverSeesPort = port;
            L($"  [解析] 服务端看到的 P2P 地址: {serverSeesIp}:{serverSeesPort}");
        }
        else
            L("  [解析] 服务端未返回 OK IP PORT（旧版服务端仅返回 OK）");
    }
    L("");

    L("--- 步骤3: 本机监听 P2P 端口并自连测试 ---");
    var listener = new TcpListener(IPAddress.Any, p2pPort);
    listener.Start();
    L($"  已在 0.0.0.0:{p2pPort} 开始监听");
    try
    {
        using var self = new TcpClient();
        self.ConnectAsync(IPAddress.Loopback, p2pPort).Wait(TimeSpan.FromSeconds(3));
        L($"  本机连接 127.0.0.1:{p2pPort}: {(self.Connected ? "成功" : "失败")}");
    }
    catch (Exception ex)
    {
        L($"  本机连接 127.0.0.1:{p2pPort}: 异常 - {ex.Message}");
    }

    if (!string.IsNullOrEmpty(serverSeesIp) && serverSeesPort > 0)
    {
        L("");
        L("--- 步骤4: 连接“服务端看到的地址”（监听仍开启，模拟对端连本机）---");
        try
        {
            using var toSelf = new TcpClient();
            toSelf.ReceiveTimeout = 5000;
            toSelf.SendTimeout = 5000;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            toSelf.ConnectAsync(serverSeesIp, serverSeesPort, cts.Token).AsTask().Wait(cts.Token);
            L($"  连接 {serverSeesIp}:{serverSeesPort}: 成功");
        }
        catch (Exception ex)
        {
            L($"  连接 {serverSeesIp}:{serverSeesPort}: 失败 - {ex.Message}");
            L("  （若本机在 NAT 后，此步骤失败常见）");
        }
    }
    listener.Stop();
    L("");

    if (!string.IsNullOrEmpty(peerId) && peerId.Length == 3)
    {
        L($"--- 步骤5: QUERY 对端 {peerId} 并尝试直连 ---");
        using (var ctrl = new TcpClient())
        {
            ctrl.Connect(serverHost, serverPort);
            var stream = ctrl.GetStream();
            using var r = new StreamReader(stream, Encoding.UTF8);
            using var w = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            w.WriteLine($"REGISTER {diagId}");
            r.ReadLine();
            w.WriteLine($"QUERY {diagId} {peerId}");
            var line = r.ReadLine();
            L($"  QUERY {diagId} {peerId} -> 收到: {line ?? "(null)"}");
            var parts = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Equals("P2P", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out var peerPort))
            {
                var peerHost = parts[1];
                L($"  对端 P2P 地址: {peerHost}:{peerPort}");
                try
                {
                    using var toPeer = new TcpClient();
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    toPeer.ConnectAsync(peerHost, peerPort, cts.Token).AsTask().Wait(cts.Token);
                    L($"  连接对端 {peerHost}:{peerPort}: 成功");
                }
                catch (Exception ex)
                {
                    L($"  连接对端 {peerHost}:{peerPort}: 失败 - {ex.Message}");
                }
            }
            else
                L("  未拿到对端 P2P 地址（对端未在线或未做 PORT_MAP）");
        }
        L("");
    }

    L("--- 诊断结束 ---");
}
catch (Exception ex)
{
    L($"诊断过程异常: {ex}");
    L(ex.StackTrace ?? "");
}

L("");
L($"结果已写入: {outPath}");
File.WriteAllText(outPath, sw.ToString(), Encoding.UTF8);
Console.WriteLine("");
Console.WriteLine("请把上述完整输出（或桌面上的诊断文件内容）复制给开发者用于排查。");
