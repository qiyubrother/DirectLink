using System.Windows;
using System.Windows.Controls;
using DirectLink.Client.ViewModels;

namespace DirectLink.Client;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        TbServer.SetBinding(System.Windows.Controls.TextBox.TextProperty, new System.Windows.Data.Binding("ServerAddress") { Source = _vm });
    }

    private async void BtnConnect_OnClick(object sender, RoutedEventArgs e)
    {
        BtnConnect.IsEnabled = false;
        try
        {
            await _vm.ConnectAsync();
            if (_vm.IsConnected)
                Close();
        }
        finally
        {
            BtnConnect.IsEnabled = true;
        }
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
