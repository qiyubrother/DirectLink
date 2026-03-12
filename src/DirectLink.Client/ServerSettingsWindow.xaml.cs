using System.Windows;
using System.Windows.Controls;
using DirectLink.Client.ViewModels;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DirectLink.Client;

public partial class ServerSettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public ServerSettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        TbServer.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("ServerAddress") { Source = _vm });
        TbClientId.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("ClientId") { Source = _vm });
        Owner = Application.Current.MainWindow;
    }

    private async void BtnConnect_OnClick(object sender, RoutedEventArgs e)
    {
        // 先将对话框中的值写回 ViewModel，避免 LostFocus 尚未触发导致仍用旧值
        _vm.ServerAddress = TbServer.Text?.Trim() ?? "";
        _vm.ClientId = TbClientId.Text?.Trim() ?? "";

        BtnConnect.IsEnabled = false;
        try
        {
            await _vm.ConnectAsync();
            if (_vm.IsConnected)
            {
                DialogResult = true;
                Close();
            }
            else
                MessageBox.Show("连接失败：\n" + _vm.StatusText, "服务器设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("连接失败：\n" + ex.Message, "服务器设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnConnect.IsEnabled = true;
        }
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
