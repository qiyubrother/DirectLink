using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    /// <summary>传输方式显示：仅中转</summary>
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
            TransferFileLogger.WriteConnectDebug("ConnectAsync", "OpenRelayReceiverAsync");
            var relayStream = await _relay.OpenRelayReceiverAsync(_listenCts.Token);
            if (relayStream != null)
                _ = Task.Run(() => RelayReceiveLoopAsync(relayStream, _listenCts.Token), _listenCts.Token);
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = true;
                StatusText = $"已连接 {host}:{port}";
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
            var fileIndex = 0;
            while (!ct.IsCancellationRequested)
            {
                fileIndex++;
                TransferFileLogger.Write("RelayRecv", $"等待第 {fileIndex} 个文件…");
                WpfApp.Application.Current?.Dispatcher.Invoke(() => { IsTransferring = true; StatusText = "等待经中转的文件…"; });
                var (savedPath, bytesReceived, hashOk) = await P2PTransferService.ReceiveFromStreamAsync(relayStream, SaveDirectory, progress, ct);
                var doBreak = bytesReceived == 0 && string.IsNullOrEmpty(savedPath);
                TransferFileLogger.Write("RelayRecv", $"第 {fileIndex} 个文件: savedPath={savedPath ?? "(null)"}, bytesReceived={bytesReceived}, hashOk={hashOk}, doBreak={doBreak}");
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
                    }
                    else if (bytesReceived > 0)
                    {
                        StatusText = "接收未完成或失败（经中转）";
                        AddLog("接收", $"[中转] 未完成，已接收 {bytesReceived} 字节");
                    }
                });
                if (doBreak) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TransferFileLogger.Write("RelayRecv", $"接收循环异常: {ex.GetType().Name} - {ex.Message}");
            WpfApp.Application.Current?.Dispatcher.Invoke(() => AddLog("中继接收", ex.Message));
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
        if (!await _relay.IsPeerOnlineAsync(PeerId))
        {
            StatusText = "对端不在线";
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
        TransferModeText = "中转";

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
            for (var i = 0; i < files.Count; i++)
            {
                var filePath = files[i];
                TransferFileLogger.Write("RelaySend", $"发送第 {i + 1}/{files.Count} 个文件: {Path.GetFileName(filePath)}");
                WpfApp.Application.Current?.Dispatcher.Invoke(() => TransferFileIndex = i + 1);
                WpfApp.Application.Current?.Dispatcher.Invoke(() => StatusText = "正在经服务器中转发送…");
                await SendOneFileWithRetryAsync(serverHost, serverPort, peerIdCopy, filePath, progress);
            }
            WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsTransferring = false;
                ProgressPercent = 0;
                ProgressDetail = "";
                TransferFileIndex = 0;
                TransferFileTotal = 0;
                if (files.Count == 1)
                    StatusText = "发送完成";
                else
                {
                    StatusText = $"已发送 {files.Count} 个文件";
                    AddLog("发送", $"到 {peerIdCopy}：共 {files.Count} 个文件");
                }
            });
        });
    }

    /// <summary>经中转发送单个文件：有限次重试 + 退避，避免无限卡住；服务端已缓冲时发送端可快速完成。</summary>
    private async Task SendOneFileWithRetryAsync(string serverHost, int serverPort, string peerIdCopy, string filePath, IProgress<TransferProgress> progress)
    {
        const int maxAttempts = 12;
        var fileName = Path.GetFileName(filePath);
        long lastBytesSent = 0;
        bool lastSuccess = false;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            if (_relay == null || _listenCts?.IsCancellationRequested == true)
            {
                TransferFileLogger.Write("RelaySend", $"发送中止（已断开）：{fileName}");
                break;
            }

            try
            {
                TransferFileLogger.Write("RelaySend", $"尝试 {attempt}/{maxAttempts} 发送: {fileName}");
                var (bytesSent, success) = await RelayService.SendFileViaRelayAsync(serverHost, serverPort, ClientId, peerIdCopy, filePath, progress);
                lastBytesSent = bytesSent;
                lastSuccess = success;
                if (success)
                {
                    WpfApp.Application.Current?.Dispatcher.Invoke(() =>
                        AddLog("发送", $"到 {peerIdCopy}：{fileName} ({bytesSent} 字节) 完成（第 {attempt} 次尝试）"));
                    return;
                }
                WpfApp.Application.Current?.Dispatcher.Invoke(() =>
                    AddLog("发送", $"到 {peerIdCopy}：{fileName} 未完成 {bytesSent} 字节（第 {attempt} 次），将重试"));
            }
            catch (Exception ex)
            {
                lastSuccess = false;
                WpfApp.Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusText = "经中转发送失败: " + ex.Message;
                    AddLog("发送", $"[中转] {fileName} 第 {attempt} 次异常: {ex.Message}");
                });
            }

            if (attempt < maxAttempts)
            {
                var delayMs = 1500 + Math.Min(attempt * 500, 5000);
                await Task.Delay(delayMs);
            }
        }

        WpfApp.Application.Current?.Dispatcher.Invoke(() =>
            AddLog("发送", $"到 {peerIdCopy}：{fileName} 已放弃（共 {attempt} 次，最后 {lastBytesSent} 字节，Success={lastSuccess}）"));
    }

    private void OnIncomingSendRequest(string senderId, string host, int port)
    {
        // 仅中转模式，不再使用 INCOMING 通知
    }

    public void Disconnect()
    {
        _ = DisconnectAsync();
    }

    public async Task DisconnectAsync()
    {
        TransferFileLogger.WriteConnectDebug("DisconnectAsync", "入口");
        _listenCts?.Cancel();
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
        IsTransferring = false;
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
