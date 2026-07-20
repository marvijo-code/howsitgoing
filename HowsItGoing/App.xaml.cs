using Uno.Resizetizer;
using HowsItGoing.Services;
using HowsItGoing.ViewModels;

namespace HowsItGoing;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    protected Window? MainWindow { get; private set; }
    public IHost? Host { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var builder = this.CreateBuilder(args)
            .Configure(host => host
#if DEBUG
                // Switch to Development environment when running in DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .UseLogging(configure: (context, logBuilder) =>
                {
                    // Configure log levels for different categories of logging
                    logBuilder
                        .SetMinimumLevel(
                            context.HostingEnvironment.IsDevelopment() ?
                                LogLevel.Information :
                                LogLevel.Warning)

                        // Default filters for core Uno Platform namespaces
                        .CoreLogLevel(LogLevel.Warning);
                }, enableUnoLogging: true)
                .UseConfiguration(configure: configBuilder =>
                    configBuilder
                        .EmbeddedSource<App>()
                        .Section<AppConfig>()
                )
                .UseHttp((context, services) => {
#if DEBUG
                // DelegatingHandler will be automatically injected
                services.AddTransient<DelegatingHandler, DebugHttpHandler>();
#endif

})
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton(_ =>
                    {
                        var configuredOptions = SharedStoreOptions.FromConfiguration(context.Configuration);
                        var localOptions = SharedStoreOptions.FromLocalJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"));
                        var options = configuredOptions.Merge(localOptions);
                        if (!options.IsConfigured)
                        {
                            // Android packages appsettings.Local.json as an embedded resource, not a loose file.
                            options = options.Merge(SharedStoreOptions.FromEmbeddedResource(typeof(App).Assembly, "appsettings.Local.json"));
                        }

                        return options;
                    });
                    services.AddSingleton<SharedBridgeStore>();
                    services.AddSingleton<AppSettingsStore>();
                    services.AddSingleton<BridgeApiClient>();
                    services.AddSingleton<MainViewModel>();
                })
            );
        MainWindow = builder.Window;

        #if DEBUG
        MainWindow.UseStudio();
#endif
#if WINDOWS
        MainWindow.SetWindowIcon();
#endif

        Host = builder.Build();

        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        if (MainWindow.Content is not Frame rootFrame)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // Place the frame in the current Window
            MainWindow.Content = rootFrame;
        }

        if (rootFrame.Content == null)
        {
            // When the navigation stack isn't restored navigate to the first page,
            // configuring the new page by passing required information as a navigation
            // parameter
            rootFrame.Navigate(typeof(MainPage), args.Arguments);
        }

        // Apply persisted theme preference (default: Dark)
        ApplyPersistedTheme(rootFrame);

        // Ensure the current window is active
        MainWindow.Activate();
    }

    private async void ApplyPersistedTheme(FrameworkElement rootElement)
    {
        try
        {
            var settingsStore = Host?.Services.GetRequiredService<AppSettingsStore>();
            if (settingsStore is null) return;

            var settings = await settingsStore.LoadAsync();
            rootElement.RequestedTheme = settings.ThemePreference switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Dark // Default to dark
            };
        }
        catch
        {
            // Fall back to dark on any error
            rootElement.RequestedTheme = ElementTheme.Dark;
        }
    }
}
