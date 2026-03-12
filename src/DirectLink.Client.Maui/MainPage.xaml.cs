using DirectLink.Client.Maui.ViewModels;

namespace DirectLink.Client.Maui;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;

    public MainPage()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        _vm.ShowAlert = (title, message) => MainThread.BeginInvokeOnMainThread(async () =>
            await DisplayAlertAsync(title, message, "确定"));
        BindingContext = _vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _vm.ConnectAsync();
    }

    protected override void OnDisappearing()
    {
        _vm.SaveConfig();
        _vm.Disconnect();
        base.OnDisappearing();
    }
}
