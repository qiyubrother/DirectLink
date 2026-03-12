using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DirectLink.Client.Config;
using DirectLink.Client.Services;
using WpfApp = System.Windows;

namespace DirectLink.Client.ViewModels;

public class MainViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _clientId = "";
    private string _serverAddress = "127.0.0.1:50000";
    private string _peerId = "";
    private string _selectedFile = "";
    private string _statusText = "未连接";
    private double _progressPercent;
    private string _progressDetail = "";
    private bool _isConnected;
    private bool _isTransferring;
    private string _transferModeText = "";
    private int _transferFileIndex;
    private int _transferFileTotal;
    private RelayService? _relay;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private Socket? _p2pSocket;
    private int _p2pPort = P2PTransferService.P2PListenPortBase;
    /// <summary>发送时等待“对端连上我们”提供的流，与“我们连对端”二选一</summary>
    private TaskCompletionSource<(Stream stream, Socket? toClose)>? _pendingSendStreamTcs;

    /// <summary>直连建立超时（秒），双方尝试连接时给足时间，避免高延迟/跨网段超时</summary>
    private const int P2PConnectTimeoutSeconds = 45;

    public MainViewModel()
    {
        var config = AppConfig.Load();
        _clientId = config.ClientId;
        _serverAddress = $"{config.ServerHost}:{config.ServerPort}";
        _saveDirectory = config.SaveDirectory;
        LogEntries = new ObservableCollection<LogEntry>();
    }

    public string ClientId
    {
        get => _clientId;
        set { _clientId = value; OnPropertyChanged(nameof(ClientId)); }
    }

    public string ServerAddress
    {
        get => _serverAddress;
        set { _serverAddress = value; OnPropertyChanged(nameof(ServerAddress)); }
    }

    public string PeerId
    {
        get => _peerId;
        set { _peerId = value; OnPropertyChanged(nameof(PeerId)); }
    }

    public string SelectedFile
    {
        get => _selectedFile;
        set { _selectedFile = value; OnPropertyChanged(nameof(SelectedFile)); OnPropertyChanged(nameof(SelectedSummary)); }
    }

    /// <summary>待发送项摘要（单文件显示路径，多则“共 N 项”）</summary>
    public string SelectedSummary
    {
        get
        {
            if (PendingPaths.Count == 0)
                return string.IsNullOrEmpty(SelectedFile) ? "" : SelectedFile;
            if (PendingPaths.Count == 1)
                return PendingPaths[0];
            return $"共 {PendingPaths.Count} 项";
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); }
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        set { _progressDetail = value; OnPropertyChanged(nameof(ProgressDetail)); }
    }

    /// <summary>传输方式显示：直连 / 中转</summary>
    public string TransferModeText
    {
        get => _transferModeText;
        set { _transferModeText = value; OnPropertyChanged(nameof(TransferModeText)); }
    }

    /// <summary>多文件发送时当前第几个（1-based）</summary>
    public int TransferFileIndex
    {
        get => _transferFileIndex;
        set { _transferFileIndex = value; OnPropertyChanged(nameof(TransferFileIndex)); OnPropertyChanged(nameof(TransferFileProgressText)); }
    }

    /// <summary>多文件发送时总文件数</summary>
    public int TransferFileTotal
    {
        get => _transferFileTotal;
        set { _transferFileTotal = value; OnPropertyChanged(nameof(TransferFileTotal)); OnPropertyChanged(nameof(TransferFileProgressText)); }
    }

    public string TransferFileProgressText =>
        _transferFileTotal > 1 ? $"{_transferFileIndex}/{_transferFileTotal} 文件" : "";

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsDisconnected)); OnPropertyChanged(nameof(SettingsButtonText)); }
    }

    /// <summary>未连接状态，用于“连接”按钮的 IsEnabled</summary>
    public bool IsDisconnected => !IsConnected;

    public string SettingsButtonText => IsConnected ? "断开" : "服务端…";

    public bool IsTransferring
    {
        get => _isTransferring;
        set { _isTransferring = value; OnPropertyChanged(nameof(IsTransferring)); }
    }

    private string _saveDirectory = "";

    public string SaveDirectory
    {
        get => _saveDirectory;
        set { _saveDirectory = value; OnPropertyChanged(nameof(SaveDirectory)); }
    }

    public ObservableCollection<LogEntry> LogEntries { get; }

    /// <summary>待发送项（文件或文件夹路径），用于拖拽与多选</summary>
    public ObservableCollection<string> PendingPaths { get; } = new();

    /// <summary>从拖拽或选择添加路径（文件与文件夹，文件夹会展开为文件列表）</summary>
    public void AddPendingPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var path = p.Trim();
            if (!PendingPaths.Contains(path))
                PendingPaths.Add(path);
        }
        OnPropertyChanged(nameof(SelectedSummary));
    }

    /// <summary>清空待发送列表并可选设置单文件</summary>
    public void ClearPendingPaths(string? singleFile = null)
    {
        PendingPaths.Clear();
        SelectedFile = singleFile ?? "";
        OnPropertyChanged(nameof(SelectedSummary));
    }

    private static List<string> ExpandToFiles(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var path = p.Trim();
            if (File.Exists(path))
                list.Add(path);
            else if (Directory.Exists(path))
                list.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
        }
        return list;
    }

    private List<string> GetFilesToSend()
    {
        var paths = PendingPaths.Count > 0 ? PendingPaths.ToList() : (string.IsNullOrEmpty(SelectedFile) ? new List<string>() : new List<string> { SelectedFile });
        return ExpandToFiles(paths);
    }

    /// <summary>导出当天日志到指定路径</summary>
    public void ExportLog(string destinationPath)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DirectLink", "logs");
            var srcFile = Path.Combine(logDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            if (!File.Exists(srcFile))
            {
                WpfApp.MessageBox.Show("当前日期没有可用日志。", "导出日志", WpfApp.MessageBoxButton.OK,
                    WpfApp.MessageBoxImage.Information);
                return;
            }
            File.Copy(srcFile, destinationPath, overwrite: true);
            WpfApp.MessageBox.Show("日志已导出到:\n" + destinationPath, "导出日志", WpfApp.MessageBoxButton.OK,
                WpfApp.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfApp.MessageBox.Show("导出日志失败:\n" + ex.Message, "导出日志", WpfApp.MessageBoxButton.OK,
                WpfApp.MessageBoxImage.Error);
        }
    }

    public async Task ConnectAsync()
    {
        TransferFileLogger.WriteConnectDebug("ConnectAsync", "入口");
        if (IsTransferring) return;
        await DisconnectAsync();
        TransferFileLogger.WriteConnectDebug("ConnectAsync", "DisconnectAsync 已完成");
        await Task.Delay(400);
        TransferFileLogger.WriteConnectDebug("ConnectAsync", "延迟 400ms 后继续");
        var parts = ServerAddress.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
        var host = parts.Length > 0 ? parts[0].Trim() : "127.0.0.1";
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 50000;
        if (ClientId.Length != 3 || !ClientId.All(char.IsDigit))
        {
            StatusText = "客户端 ID 须为 3 位数字";
            return;
        }
        StatusText = "正在连接…";
        await Task.Yield();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    TransferFileLogger.WriteConnectDebug("ConnectAsync", "重试连接(1秒后)");
                    await Task.Delay(1000);
                }
                try
                {
                    TransferFileLogger.WriteConnectDebug("ConnectAsync", $"创建 RelayService {host}:{port}");
                    _relay = new RelayService(host, port, ClientId);
                    _relay.IncomingSendRequest += OnIncomingSendRequest;
                    TransferFileLogger.WriteConnectDebug("ConnectAsync", "调用 ConnectAndRegisterAsync");
                    await _relay.ConnectAndRegisterAsync();
                    break;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    TransferFileLogger.WriteConnectDebug("ConnectAsync", "首次连接失败: " + ex.Message);
                    var failed = _relay;
                    _relay = null;
                    if (failed != null) await failed.DisposeAsync();
                }
            }
            if (_relay == null)
                throw new InvalidOperationException("连接服务器失败，请稍后重试");
            TransferFileLogger.WriteConnectDebug("ConnectAsync", "ConnectAndRegisterAsync 完成");
            _listenCts = new CancellationTokenSource();
            TransferFileLogger.WriteConnectDebug("ConnectAsync", "创建 P2P Socket 并 Bind(0) 由系统分配端口");
            _p2pSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _p2pSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _p2pSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
            _p2pSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            _p2pPort = ((IPEndPoint)_p2pSocket.LocalEndPoint!).Port;
            TransferFileLogger.WriteConnectDebug("ConnectAsync", $"P2P 端口已分配: {_p2pPort}");
            _p2pSocket.Listen(100);
            TransferFileLogger.WriteConnectDebug("ConnectAsync", $"向服务器上报 P2P 端口 {_p2pPort}");
            await RelayService.ReportP2PPortAsync(host, port, ClientId, _p2pPort, _listenCts.Token);
            TransferFileLogger.WriteConnectDebug("ConnectAsync", "ReportP2PPort 完成，启动 AcceptLoop");
            _listenTask = Task.Run(() => AcceptLoopAsync(_listenCts.Token), _listenCts.Token);
            TransferFileLogger.WriteConnectDebug("ConnectAsync", "OpenRelayReceiverAsync");
            var relayStream = await _relay.OpenRelayReceiverAsync(_listenCts.Token);
            if (relayStream != null)
                _ = Task.Run(() => RelayReceiveLoopAsync(relayStream, _listenCts.Token), _listenCts.Token);
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = true;
                StatusText = $"已连接 {host}:{port}，P2P 端口 {_p2pPort}";
            });
            TransferFileLogger.WriteConnectDebug("ConnectAsync", "连接成功");
            AddLog("连接", $"已连接中继服务器 {host}:{port}");
        }
        catch (Exception ex)
        {
            var full = ex.ToString();
            TransferFileLogger.WriteConnectDebug("ConnectAsync 异常", full);
            var msg = ex.Message;
            WpfApp.Application.Current?.Dispatcher.Invoke(() => StatusText = "连接失败: " + msg);
            AddLog("错误", msg);
        }
    }

    private async Task RelayReceiveLoopAsync(Stream relayStream, CancellationToken ct)
    {
        var progress = new Progress<TransferProgress>(p =>
        {
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                ProgressPercent = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
                ProgressDetail = $"{FormatSize(p.Current)} / {FormatSize(p.Total)} {FormatSpeed(p.BytesPerSecond)}";
            });
        });
        try
        {
            while (!ct.IsCancellationRequested)
            {
                WpfApp.Application.Current?.Dispatcher.Invoke(() => { IsTransferring = true; StatusText = "等待经中转的文件…"; });
                var (savedPath, bytesReceived, hashOk) = await P2PTransferService.ReceiveFromStreamAsync(relayStream, SaveDirectory, progress, ct);
                WpfApp.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsTransferring = false;
                    ProgressPercent = 0;
                    ProgressDetail = "";
                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        TransferModeText = "中转";
                        StatusText = hashOk ? "接收完成（经中转），哈希校验通过" : "接收完成（经中转），哈希校验失败";
                        AddLog("接收", $"[中转] {savedPath} ({bytesReceived} 字节) 校验: {(hashOk ? "通过" : "失败")}");
                        WpfApp.MessageBox.Show(
                            hashOk ? $"文件已保存（经服务器中转）。\n{savedPath}\n哈希校验通过。" : $"文件已保存（经服务器中转）。\n{savedPath}\n哈希校验未通过。",
                            "传输完成",
                            WpfApp.MessageBoxButton.OK,
                            hashOk ? WpfApp.MessageBoxImage.Information : WpfApp.MessageBoxImage.Warning);
                    }
                    else if (bytesReceived > 0)
                    {
                        StatusText = "接收未完成或失败（经中转）";
                        AddLog("接收", $"[中转] 未完成，已接收 {bytesReceived} 字节");
                    }
                });
                if (bytesReceived == 0 && string.IsNullOrEmpty(savedPath)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WpfApp.Application.Current?.Dispatcher.Invoke(() => AddLog("中继接收", ex.Message));
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var socket = _p2pSocket;
        if (socket == null) { TransferFileLogger.WriteConnectDebug("AcceptLoop", "socket==null 直接返回"); return; }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket? accepted = null;
                try
                {
                    accepted = await socket.AcceptAsync(ct);
                    var stream = new NetworkStream(accepted, ownsSocket: false);
                    if (TryOfferAcceptedStream(stream, accepted))
                        continue;
                    _ = HandleIncomingConnectionAsync(stream, accepted, ct);
                }
                catch (Exception)
                {
                    try { accepted?.Close(); } catch { }
                }
            }
            TransferFileLogger.WriteConnectDebug("AcceptLoop", "循环退出(已取消)");
        }
        catch (OperationCanceledException) { TransferFileLogger.WriteConnectDebug("AcceptLoop", "OperationCanceledException 退出"); }
        catch (ObjectDisposedException) { TransferFileLogger.WriteConnectDebug("AcceptLoop", "ObjectDisposedException 退出(Socket已Close)"); }
        catch (Exception ex)
        {
            TransferFileLogger.WriteConnectDebug("AcceptLoop 异常", ex.GetType().Name + ": " + ex.Message);
            WpfApp.Application.Current?.Dispatcher.Invoke(() => AddLog("监听", ex.Message));
        }
    }

    private async Task HandleIncomingConnectionAsync(Stream stream, Socket? toClose, CancellationToken ct)
    {
        try
        {
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsTransferring = true;
                StatusText = "正在接收文件…";
            });
            var progress = new Progress<TransferProgress>(p =>
            {
                WpfApp.Application.Current?.Dispatcher.Invoke(() =>
                {
                    ProgressPercent = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
                    var speed = p.BytesPerSecond > 0 ? $"{FormatSpeed(p.BytesPerSecond)}" : "";
                    ProgressDetail = $"{FormatSize(p.Current)} / {FormatSize(p.Total)} {speed}";
                });
            });
            var (savedPath, bytesReceived, hashOk) = await P2PTransferService.ReceiveFromStreamAsync(stream, SaveDirectory, progress, ct);
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsTransferring = false;
                ProgressPercent = 0;
                ProgressDetail = "";
                if (!string.IsNullOrEmpty(savedPath))
                {
                    TransferModeText = "直连";
                    StatusText = hashOk ? "接收完成，哈希校验通过" : "接收完成，哈希校验失败";
                    AddLog("接收", $"{savedPath} ({bytesReceived} 字节) 校验: {(hashOk ? "通过" : "失败")}");
                    WpfApp.MessageBox.Show(
                        hashOk ? $"文件已保存到:\n{savedPath}\n哈希校验通过。" : $"文件已保存到:\n{savedPath}\n哈希校验未通过，请核对。",
                        "传输完成",
                        WpfApp.MessageBoxButton.OK,
                        hashOk ? WpfApp.MessageBoxImage.Information : WpfApp.MessageBoxImage.Warning);
                }
                else
                {
                    StatusText = "接收未完成或失败";
                    AddLog("接收", $"未完成，已接收 {bytesReceived} 字节");
                }
            });
        }
        finally
        {
            try { toClose?.Close(); } catch { }
        }
    }

    public async Task SendFileAsync()
    {
        if (_relay == null || !IsConnected)
        {
            StatusText = "请先连接服务器";
            return;
        }
        if (PeerId.Length != 3 || !PeerId.All(char.IsDigit))
        {
            StatusText = "对端 ID 须为 3 位数字";
            return;
        }
        var files = GetFilesToSend();
        if (files.Count == 0)
        {
            StatusText = "请选择或拖入要发送的文件/文件夹";
            return;
        }
        var (peerHost, peerPort) = await _relay.QueryPeerAsync(PeerId);
        if (peerHost == null || peerPort == null)
        {
            StatusText = "对端不在线或未上报 P2P 端口";
            AddLog("发送", "对端不可达");
            return;
        }
        var serverParts = ServerAddress.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
        var serverHost = serverParts.Length > 0 ? serverParts[0].Trim() : "127.0.0.1";
        var serverPort = serverParts.Length > 1 && int.TryParse(serverParts[1], out var sp) ? sp : 50000;
        var peerIdCopy = PeerId;
        IsTransferring = true;
        TransferFileTotal = files.Count;
        TransferFileIndex = 0;
        TransferModeText = "";

        var progress = new Progress<TransferProgress>(p =>
        {
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                ProgressPercent = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
                var detail = $"{FormatSize(p.Current)} / {FormatSize(p.Total)} {FormatSpeed(p.BytesPerSecond)}";
                if (TransferFileTotal > 1)
                    detail = $"{TransferFileIndex}/{TransferFileTotal} 文件 · " + detail;
                ProgressDetail = detail;
            });
        });

        _ = Task.Run(async () =>
        {
            var lastMode = "";
            for (var i = 0; i < files.Count; i++)
            {
                var filePath = files[i];
                WpfApp.Application.Current?.Dispatcher.Invoke(() => TransferFileIndex = i + 1);
                var usedRelay = await SendOneFileAsync(peerHost, peerPort.Value, serverHost, serverPort, peerIdCopy, filePath, progress);
                lastMode = usedRelay ? "中转" : "直连";
            }
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsTransferring = false;
                ProgressPercent = 0;
                ProgressDetail = "";
                TransferFileIndex = 0;
                TransferFileTotal = 0;
                TransferModeText = lastMode;
                if (files.Count == 1)
                    StatusText = "发送完成";
                else
                {
                    StatusText = $"已发送 {files.Count} 个文件（{lastMode}）";
                    AddLog("发送", $"到 {peerIdCopy}：共 {files.Count} 个文件，{lastMode}");
                }
            });
        });
    }

    /// <summary>发送单个文件，先直连失败则尝试中转。返回是否使用了中转。</summary>
    private async Task<bool> SendOneFileAsync(string peerHost, int peerPort, string serverHost, int serverPort, string peerIdCopy, string filePath, IProgress<TransferProgress> progress)
    {
        System.Net.Sockets.TcpClient? toClose = null;
        try
        {
            WpfApp.Application.Current?.Dispatcher.Invoke(() => StatusText = "正在建立直连…");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(P2PConnectTimeoutSeconds));
            var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(peerHost, peerPort, cts.Token);
            toClose = client;
            var stream = client.GetStream();
            WpfApp.Application.Current?.Dispatcher.Invoke(() => StatusText = "正在发送…");
            var (bytesSent, success) = await P2PTransferService.SendFileToStreamAsync(stream, filePath, 0, progress);
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (success)
                    AddLog("发送", $"到 {peerIdCopy}：{Path.GetFileName(filePath)} ({bytesSent} 字节) 直连完成");
            });
            return false;
        }
        catch (OperationCanceledException)
        {
            return await TryRelayOnDirectFailureAsync(serverHost, serverPort, peerIdCopy, filePath, progress, "建立直连超时");
        }
        catch (Exception ex)
        {
            return await TryRelayOnDirectFailureAsync(serverHost, serverPort, peerIdCopy, filePath, progress, "发送失败: " + ex.Message);
        }
        finally
        {
            try { toClose?.Close(); } catch { }
        }
    }

    private async Task<bool> TryRelayOnDirectFailureAsync(string serverHost, int serverPort, string peerIdCopy, string filePath, IProgress<TransferProgress> progress, string failureMessage)
    {
        var useRelay = false;
        WpfApp.Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusText = failureMessage;
            AddLog("发送", failureMessage);
            useRelay = WpfApp.MessageBox.Show(
                "直连失败，是否经服务器中转？\n（会占用服务器带宽）",
                "经服务器中转",
                WpfApp.MessageBoxButton.YesNo,
                WpfApp.MessageBoxImage.Question) == WpfApp.MessageBoxResult.Yes;
        });
        if (!useRelay) return false;
        WpfApp.Application.Current?.Dispatcher.Invoke(() => StatusText = "正在经服务器中转发送…");
        try
        {
            var (bytesSent, success) = await RelayService.SendFileViaRelayAsync(serverHost, serverPort, ClientId, peerIdCopy, filePath, progress);
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (success)
                    AddLog("发送", $"[中转] 到 {peerIdCopy}：{Path.GetFileName(filePath)} ({bytesSent} 字节) 完成");
                else
                    AddLog("发送", $"[中转] 到 {peerIdCopy}：已发送 {bytesSent} 字节，未完成");
            });
            return true;
        }
        catch (Exception ex)
        {
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusText = "经中转发送失败: " + ex.Message;
                AddLog("发送", "[中转] " + ex.Message);
            });
            return false;
        }
    }

    /// <summary>
    /// 收到服务端下发的 INCOMING：发送方将主动连到本机，本机只做监听、不主动连发送方，避免 NAT 导致“连发送方超时”报错。
    /// </summary>
    private void OnIncomingSendRequest(string senderId, string host, int port)
    {
        WpfApp.Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusText = $"发送方 {senderId} 将连接至本机，请稍候…";
            AddLog("接收", $"发送方 {senderId} 准备发送，等待对方连入…");
        });
    }

    /// <summary>由监听线程调用：若当前有“等待对端连上来发文件”，则把接受的连接交给发送路径并返回 true。</summary>
    public bool TryOfferAcceptedStream(Stream stream, Socket? toClose)
    {
        var tcs = _pendingSendStreamTcs;
        if (tcs == null) return false;
        if (!tcs.TrySetResult((stream, toClose))) return false;
        _pendingSendStreamTcs = null;
        return true;
    }

    public void Disconnect()
    {
        _ = DisconnectAsync();
    }

    public async Task DisconnectAsync()
    {
        TransferFileLogger.WriteConnectDebug("DisconnectAsync", "入口");
        _listenCts?.Cancel();
        _pendingSendStreamTcs?.TrySetCanceled();
        var listenTask = _listenTask;
        _listenTask = null;
        TransferFileLogger.WriteConnectDebug("DisconnectAsync", "关闭 P2P Socket 以唤醒 AcceptAsync");
        try { _p2pSocket?.Close(); } catch (Exception ex) { TransferFileLogger.WriteConnectDebug("DisconnectAsync Close", ex.Message); }
        _p2pSocket = null;
        if (listenTask != null)
        {
            TransferFileLogger.WriteConnectDebug("DisconnectAsync", "等待监听任务退出(最多3秒)");
            try { await listenTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) { TransferFileLogger.WriteConnectDebug("DisconnectAsync WaitAsync", ex.Message); }
            TransferFileLogger.WriteConnectDebug("DisconnectAsync", "监听任务已退出");
        }
        var r = _relay;
        _relay = null;
        if (r != null)
        {
            TransferFileLogger.WriteConnectDebug("DisconnectAsync", "DisposeAsync 中继");
            r.IncomingSendRequest -= OnIncomingSendRequest;
            await r.DisposeAsync();
            TransferFileLogger.WriteConnectDebug("DisconnectAsync", "中继已释放");
        }
        WpfApp.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsConnected = false;
            StatusText = "未连接";
        });
        TransferFileLogger.WriteConnectDebug("DisconnectAsync", "完成");
        AddLog("连接", "已断开");
    }

    public void SaveConfig()
    {
        var parts = ServerAddress.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
        var config = new AppConfig
        {
            ClientId = ClientId,
            ServerHost = parts.Length > 0 ? parts[0].Trim() : "127.0.0.1",
            ServerPort = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 50000,
            SaveDirectory = SaveDirectory
        };
        config.Save();
    }

    private void AddLog(string category, string message)
    {
        TransferFileLogger.Write(category, message);
        WpfApp.Application.Current?.Dispatcher.Invoke(() =>
        {
            LogEntries.Insert(0, new LogEntry(DateTime.Now, category, message));
            if (LogEntries.Count > 500)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        return $"{FormatSize((long)bytesPerSecond)}/s";
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public record LogEntry(DateTime Time, string Category, string Message);
