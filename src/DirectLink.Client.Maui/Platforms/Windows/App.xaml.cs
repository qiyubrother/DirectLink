namespace DirectLink.Client.Maui.Platforms.Windows;

public partial class App : Microsoft.Maui.MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
