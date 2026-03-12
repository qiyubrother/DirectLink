using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    private Task? _listenTask;
    private TcpListener? _p2pListener;
    private int _p2pPort = P2PTransferService.P2PListenPortBase;
    private const int P2PConnectTimeoutSeconds = 45;

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
            try { _p2pListener?.Stop(); } catch { }
            _p2pListener = null;
            _listenCts?.Cancel();
            await (_relay?.DisposeAsync() ?? ValueTask.CompletedTask);
            _relay = new RelayService(host, port, ClientId);
            _relay.IncomingSendRequest += OnIncomingSendRequest;
            await _relay.ConnectAndRegisterAsync();
            _listenCts = new CancellationTokenSource();
            _p2pPort = P2PTransferService.P2PListenPortBase + int.Parse(ClientId) % 1000;
            await RelayService.ReportP2PPortAsync(host, port, ClientId, _p2pPort, _listenCts.Token);
            _p2pListener = new TcpListener(IPAddress.IPv6Any, _p2pPort);
            _p2pListener.Server.DualMode = true;
            _p2pListener.Start();
            _listenTask = Task.Run(() => AcceptLoopAsync(_listenCts.Token), _listenCts.Token);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = true;
                StatusText = $"已连接 {host}:{port}，P2P 端口 {_p2pPort}";
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

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _p2pListener;
        if (listener == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                try
                {
                    var stream = client.GetStream();
                    if (TryOfferAcceptedStream(stream, client)) continue;
                }
                catch { }
                _ = HandleIncomingConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() => AddLog("监听", ex.Message));
        }
    }

    private async Task HandleIncomingConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var stream = client.GetStream();
            MainThread.BeginInvokeOnMainThread(() => { IsTransferring = true; StatusText = "正在接收文件…"; });
            var progress = new Progress<TransferProgress>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ProgressPercent = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
                    ProgressDetail = $"{FormatSize(p.Current)} / {FormatSize(p.Total)} {FormatSpeed(p.BytesPerSecond)}";
                });
            });
            var saveDir = string.IsNullOrWhiteSpace(SaveDirectory)
                ? Path.Combine(FileSystem.AppDataDirectory, "DirectLinkReceived")
                : SaveDirectory;
            var (savedPath, bytesReceived, hashOk) = await P2PTransferService.ReceiveFromStreamAsync(stream, saveDir, progress, ct);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsTransferring = false;
                ProgressPercent = 0;
                ProgressDetail = "";
                if (!string.IsNullOrEmpty(savedPath))
                {
                    StatusText = hashOk ? "接收完成，哈希校验通过" : "接收完成，哈希校验失败";
                    AddLog("接收", $"{savedPath} ({bytesReceived} 字节) 校验: {(hashOk ? "通过" : "失败")}");
                    ShowAlert?.Invoke("传输完成", hashOk ? $"文件已保存。\n{savedPath}\n哈希校验通过。" : $"文件已保存。\n{savedPath}\n哈希校验未通过。");
                }
                else
                {
                    StatusText = "接收未完成或失败";
                    AddLog("接收", $"未完成，已接收 {bytesReceived} 字节");
                }
            });
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsTransferring = false;
                ProgressPercent = 0;
                ProgressDetail = "";
                StatusText = "接收异常";
                AddLog("接收", "异常: " + ex.Message);
            });
        }
        finally
        {
            try { client.Close(); } catch { }
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
        var (peerHost, peerPort) = await _relay.QueryPeerAsync(PeerId);
        if (peerHost == null || peerPort == null)
        {
            StatusText = "对端不在线或未上报 P2P 端口";
            AddLog("发送", "对端不可达");
            return;
        }
        IsTransferring = true;
        StatusText = "正在建立直连…";
        var peerHostCopy = peerHost;
        var peerPortCopy = peerPort.Value;
        var filePath = SelectedFile;
        var peerIdCopy = PeerId;
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
            TcpClient? toClose = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(P2PConnectTimeoutSeconds));
                var client = new TcpClient();
                await client.ConnectAsync(peerHostCopy, peerPortCopy, cts.Token);
                toClose = client;
                var stream = client.GetStream();
                MainThread.BeginInvokeOnMainThread(() => StatusText = "正在发送…");
                var (bytesSent, success) = await P2PTransferService.SendFileToStreamAsync(stream, filePath, 0, progress);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsTransferring = false;
                    ProgressPercent = 0;
                    ProgressDetail = "";
                    if (success)
                    {
                        StatusText = "发送完成，已校验";
                        AddLog("发送", $"到 {peerIdCopy}：{Path.GetFileName(filePath)} ({bytesSent} 字节) 完成");
                        ShowAlert?.Invoke("传输完成", $"已向 {peerIdCopy} 发送完成。\n文件: {Path.GetFileName(filePath)}\n大小: {FormatSize(bytesSent)}");
                    }
                    else
                    {
                        StatusText = "发送未完成";
                        AddLog("发送", $"到 {peerIdCopy}：已发送 {bytesSent} 字节，未完成");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsTransferring = false;
                    ProgressPercent = 0;
                    ProgressDetail = "";
                    StatusText = "建立直连超时，请确认对端已连接且网络可达";
                    AddLog("发送", "建立直连超时");
                });
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsTransferring = false;
                    ProgressPercent = 0;
                    ProgressDetail = "";
                    StatusText = "发送失败: " + ex.Message;
                    AddLog("发送", ex.Message);
                });
            }
            finally
            {
                try { toClose?.Close(); } catch { }
            }
        });
    }

    private void OnIncomingSendRequest(string senderId, string host, int port)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = $"发送方 {senderId} 将连接至本机，请稍候…";
            AddLog("接收", $"发送方 {senderId} 准备发送，等待对方连入…");
        });
    }

    public bool TryOfferAcceptedStream(Stream stream, TcpClient client) => false;

    public void Disconnect()
    {
        _listenCts?.Cancel();
        try { _p2pListener?.Stop(); } catch { }
        _p2pListener = null;
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
