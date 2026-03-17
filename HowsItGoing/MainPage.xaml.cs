using HowsItGoing.Services;
using HowsItGoing.ViewModels;

namespace HowsItGoing;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        this.InitializeComponent();
        ViewModel = ((App)Application.Current).Host?.Services.GetRequiredService<MainViewModel>()
            ?? throw new InvalidOperationException("MainViewModel is not registered.");
        DataContext = ViewModel;
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        await MonitoringServiceCoordinator.SyncAsync(ViewModel.MonitoringEnabled);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveSettingsAsync();
        await MonitoringServiceCoordinator.SyncAsync(ViewModel.MonitoringEnabled);
    }

    private async void StartAgent_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartAgentAsync();
    }

    private async void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.ReleaseUrl))
        {
            await ReleaseInstaller.LaunchAsync(ViewModel.ReleaseUrl);
        }
    }
}
