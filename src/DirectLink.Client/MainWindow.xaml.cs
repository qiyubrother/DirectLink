using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirectLink.Client.ViewModels;
using Win32Dialog = Microsoft.Win32;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace DirectLink.Client;

public partial class MainWindow
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        TbClientId.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("ClientId"));
        TbPeerId.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("PeerId"));
        TbStatus.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("StatusText"));
        ProgressBar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new System.Windows.Data.Binding("ProgressPercent"));
        TbProgressDetail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ProgressDetail"));
        TbSaveDir.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("SaveDirectory"));
        BtnSend.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsConnected"));

        // Alt+S 发送
        InputBindings.Add(new KeyBinding(
            new RelayCommand(OnSendKey),
            Key.S, ModifierKeys.Alt));
    }

    private void OnSendKey()
    {
        if (_vm != null && _vm.IsConnected)
            _ = _vm.SendFileAsync();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _ = TryAutoConnectAsync();
    }

    private async System.Threading.Tasks.Task TryAutoConnectAsync()
    {
        try
        {
            await _vm!.ConnectAsync();
            if (!_vm.IsConnected)
                MessageBox.Show("无法连接服务器，请点击「服务端…」设置服务端地址及端口后重试。", "连接失败",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else if (Environment.GetEnvironmentVariable("DIRECTLINK_DEBUG_RECONNECT") == "1")
                _ = RunReconnectTestAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动时连接失败: " + ex.Message, "连接失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async System.Threading.Tasks.Task RunReconnectTestAsync()
    {
        await Task.Delay(2000);
        if (_vm == null || !_vm.IsConnected) return;
        await _vm.DisconnectAsync();
        await Task.Delay(1000);
        await _vm.ConnectAsync();
    }

    private void BtnSettingsOrDisconnect_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (_vm.IsConnected)
        {
            _vm.Disconnect();
            return;
        }
        var dlg = new ServerSettingsWindow(_vm);
        dlg.ShowDialog();
    }

    private void BtnSelectFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Win32Dialog.OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog() == true && _vm != null)
        {
            if (dlg.FileNames.Length == 1)
            {
                _vm.SelectedFile = dlg.FileNames[0];
                _vm.ClearPendingPaths(dlg.FileNames[0]);
            }
            else
            {
                _vm.ClearPendingPaths();
                _vm.AddPendingPaths(dlg.FileNames);
            }
        }
    }

    private void BtnSend_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
            _ = _vm.SendFileAsync();
    }

    private void BtnBrowseSave_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择接收文件保存目录",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && _vm != null)
            _vm.SaveDirectory = dlg.SelectedPath;
    }

    private void DropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _vm == null) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths == null || paths.Length == 0) return;
        if (paths.Length == 1)
        {
            _vm.ClearPendingPaths(paths[0]);
        }
        else
        {
            _vm.ClearPendingPaths();
            _vm.AddPendingPaths(paths);
        }
        e.Handled = true;
    }

    private async void BtnConnect_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.ConnectAsync();
        if (!_vm.IsConnected)
        {
            var detail = _vm.StatusText;
            var msg = string.IsNullOrEmpty(detail) || detail == "未连接"
                ? "无法连接服务器，请点击「设置」检查服务端地址及端口后重试。"
                : detail;
            MessageBox.Show(msg, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDisconnect_OnClick(object sender, RoutedEventArgs e)
    {
        _vm?.Disconnect();
    }

    private void BtnSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var dlg = new ServerSettingsWindow(_vm);
        dlg.ShowDialog();
    }

    private void BtnExportLog_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var dlg = new Win32Dialog.SaveFileDialog
        {
            Title = "导出日志",
            Filter = "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            FileName = $"DirectLink_{DateTime.Now:yyyyMMdd}.log"
        };
        if (dlg.ShowDialog() == true)
            _vm.ExportLog(dlg.FileName);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm?.SaveConfig();
        _vm?.Disconnect();
        base.OnClosed(e);
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged;
}
