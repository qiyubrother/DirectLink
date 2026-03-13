using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DirectLink.Client.Maui.Config;
using DirectLink.Client.Maui.Services;
using Microsoft.Maui.Storage;

namespace DirectLink.Client.Maui.ViewModels;

public class MainViewModel : INotifyPropertyChanged
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
    private string _saveDirectory = "";

    public string ClientId { get => _clientId; set { _clientId = value; OnPropertyChanged(); } }
    public string ServerAddress { get => _serverAddress; set { _serverAddress = value; OnPropertyChanged(); } }
    public string PeerId { get => _peerId; set { _peerId = value; OnPropertyChanged(); } }
    public string SelectedFile { get => _selectedFile; set { _selectedFile = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public double ProgressPercent { get => _progressPercent; set { _progressPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressRatio)); } }
    public double ProgressRatio => Math.Max(0, Math.Min(1, _progressPercent / 100.0));
    public string ProgressDetail { get => _progressDetail; set { _progressDetail = value; OnPropertyChanged(); } }
    public bool IsConnected { get => _isConnected; set { _isConnected = value; OnPropertyChanged(); } }
    public bool IsTransferring { get => _isTransferring; set { _isTransferring = value; OnPropertyChanged(); } }
    public string SaveDirectory { get => _saveDirectory; set { _saveDirectory = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public ICommand ConnectCommand => new Command(async () => await ConnectAsync());
    public ICommand DisconnectCommand => new Command(Disconnect);
    public ICommand SendCommand => new Command(async () => await SendFileAsync());
    public ICommand PickFileCommand => new Command(PickFile);

    private RelayService? _relay;
    private CancellationTokenSource? _listenCts;

    /// <summary>由页面设置，用于显示弹窗（标题, 消息）</summary>
    public Action<string, string>? ShowAlert { get; set; }

    public MainViewModel()
    {
        var config = AppConfig.Load();
        _clientId = config.ClientId;
        _serverAddress = $"{config.ServerHost}:{config.ServerPort}";
        _saveDirectory = config.SaveDirectory;
    }

    public async Task ConnectAsync()
    {
        if (IsTransferring) return;
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
            _listenCts?.Cancel();
            await (_relay?.DisposeAsync() ?? ValueTask.CompletedTask);
            _relay = new RelayService(host, port, ClientId);
            _relay.IncomingSendRequest += OnIncomingSendRequest;
            await _relay.ConnectAndRegisterAsync();
            _listenCts = new CancellationTokenSource();
            var relayStream = await _relay.OpenRelayReceiverAsync(_listenCts.Token);
            if (relayStream != null)
                _ = Task.Run(() => RelayReceiveLoopAsync(relayStream, _listenCts!.Token), _listenCts.Token);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = true;
                StatusText = $"已连接 {host}:{port}";
            });
            AddLog("连接", $"已连接中继服务器 {host}:{port}");
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            MainThread.BeginInvokeOnMainThread(() => StatusText = "连接失败: " + msg);
            AddLog("错误", msg);
        }
    }

    private async Task RelayReceiveLoopAsync(Stream relayStream, CancellationToken ct)
    {
        var saveDir = string.IsNullOrWhiteSpace(SaveDirectory)
            ? Path.Combine(FileSystem.AppDataDirectory, "DirectLinkReceived")
            : SaveDirectory;
        var progress = new Progress<TransferProgress>(p =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
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
                MainThread.BeginInvokeOnMainThread(() => { IsTransferring = true; StatusText = "等待经中转的文件…"; });
                var (savedPath, bytesReceived, hashOk) = await P2PTransferService.ReceiveFromStreamAsync(relayStream, saveDir, progress, ct);
                var doBreak = bytesReceived == 0 && string.IsNullOrEmpty(savedPath);
                TransferFileLogger.Write("RelayRecv", $"第 {fileIndex} 个文件: savedPath={savedPath ?? "(null)"}, bytesReceived={bytesReceived}, hashOk={hashOk}, doBreak={doBreak}");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsTransferring = false;
                    ProgressPercent = 0;
                    ProgressDetail = "";
                    if (!string.IsNullOrEmpty(savedPath))
                    {
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
            MainThread.BeginInvokeOnMainThread(() => AddLog("中继接收", ex.Message));
        }
    }

    public async Task SendFileAsync()
    {
        if (_relay == null || !IsConnected || string.IsNullOrEmpty(SelectedFile) || !File.Exists(SelectedFile))
        {
            StatusText = "请先连接并选择要发送的文件";
            return;
        }
        if (PeerId.Length != 3 || !PeerId.All(char.IsDigit))
        {
            StatusText = "对端 ID 须为 3 位数字";
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
        var filePath = SelectedFile;
        var peerIdCopy = PeerId;
        IsTransferring = true;
        StatusText = "正在经服务器中转发送…";
        var progress = new Progress<TransferProgress>(p =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ProgressPercent = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
                ProgressDetail = $"{FormatSize(p.Current)} / {FormatSize(p.Total)} {FormatSpeed(p.BytesPerSecond)}";
            });
        });

        _ = Task.Run(async () =>
        {
            await SendSingleFileWithRetryAsync(serverHost, serverPort, peerIdCopy, filePath, progress);
        });
    }

    private async Task SendSingleFileWithRetryAsync(string serverHost, int serverPort, string peerIdCopy, string filePath, IProgress<TransferProgress> progress)
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
                TransferFileLogger.Write("RelaySend", $"(Maui) 发送中止（已断开）：{fileName}");
                break;
            }

            try
            {
                TransferFileLogger.Write("RelaySend", $"(Maui) 尝试 {attempt}/{maxAttempts} 发送: {fileName}");
                var (bytesSent, success) = await RelayService.SendFileViaRelayAsync(serverHost, serverPort, ClientId, peerIdCopy, filePath, progress);
                lastBytesSent = bytesSent;
                lastSuccess = success;
                if (success)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        IsTransferring = false;
                        ProgressPercent = 0;
                        ProgressDetail = "";
                        StatusText = "发送完成";
                        AddLog("发送", $"到 {peerIdCopy}：{fileName} ({bytesSent} 字节) 完成（第 {attempt} 次尝试）");
                    });
                    return;
                }
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusText = "发送未完成，将重试";
                    AddLog("发送", $"到 {peerIdCopy}：{fileName} 未完成（第 {attempt} 次）");
                });
            }
            catch (Exception ex)
            {
                lastSuccess = false;
                MainThread.BeginInvokeOnMainThread(() =>
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

        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsTransferring = false;
            ProgressPercent = 0;
            ProgressDetail = "";
            StatusText = "发送失败";
            AddLog("发送", $"到 {peerIdCopy}：{fileName} 已放弃（共 {attempt} 次，Success={lastSuccess}）");
        });
    }

    private void OnIncomingSendRequest(string senderId, string host, int port)
    {
        // 仅中转模式，不再使用 INCOMING 通知
    }

    public void Disconnect()
    {
        _listenCts?.Cancel();
        var r = _relay;
        _relay = null;
        if (r != null)
        {
            r.IncomingSendRequest -= OnIncomingSendRequest;
            _ = r.DisposeAsync();
        }
        IsConnected = false;
        StatusText = "未连接";
        AddLog("连接", "已断开");
    }

    private async void PickFile()
    {
        try
        {
            var result = await FilePicker.Default.PickAsync();
            if (result != null)
                SelectedFile = result.FullPath;
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() => AddLog("错误", "选择文件: " + ex.Message));
        }
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
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LogEntries.Insert(0, new LogEntry(DateTime.Now, category, message));
            while (LogEntries.Count > 500) LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string FormatSpeed(double bps) => $"{FormatSize((long)bps)}/s";
}

public record LogEntry(DateTime Time, string Category, string Message);
