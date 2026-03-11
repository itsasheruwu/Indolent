using Indolent.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Indolent
{
    public partial class App : Application
    {
        private static readonly string StartupLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Indolent",
            "startup.log");
        private MainWindow? mainWindow;
        private WidgetWindow? widgetWindow;
        private bool isShuttingDown;

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.SetBasePath(AppContext.BaseDirectory);
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddDebug();
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<AppState>();
                    services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                    services.AddSingleton<ICodexCliService, CodexCliService>();
                    services.AddSingleton<ICodexModelCatalogService, CodexModelCatalogService>();
                    services.AddSingleton<IProviderRuntime, OpenAiCodexProviderRuntime>();
                    services.AddSingleton<IProviderRuntime, OpenCodeProviderRuntime>();
                    services.AddSingleton<IProviderRuntimeRegistry, ProviderRuntimeRegistry>();
                    services.AddSingleton<IOpenCodeSetupService, OpenCodeSetupService>();
                    services.AddSingleton<IOcrService, WindowsOcrService>();
                    services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
                    services.AddSingleton<IAgentClickService, AgentClickService>();
                    services.AddSingleton<ITrayService, TrayService>();
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<WidgetWindowViewModel>();
                })
                .Build();
        }

        public IHost Host { get; }

        public static App CurrentApp => (App)Current;

        public bool IsShuttingDown => isShuttingDown;

        public MainWindow? MainWindowInstance => mainWindow;

        public WidgetWindow? WidgetWindowInstance => widgetWindow;

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            await Host.StartAsync();

            var settingsStore = Host.Services.GetRequiredService<ISettingsStore>();
            var providerRegistry = Host.Services.GetRequiredService<IProviderRuntimeRegistry>();
            var state = Host.Services.GetRequiredService<AppState>();
            await state.InitializeAsync(settingsStore, providerRegistry);

            mainWindow = new MainWindow(Host.Services.GetRequiredService<MainWindowViewModel>());
            widgetWindow = new WidgetWindow(Host.Services.GetRequiredService<WidgetWindowViewModel>());

            mainWindow.InitializeWindow();
            widgetWindow.InitializeWindow();

            Host.Services.GetRequiredService<ITrayService>().Initialize(
                mainWindow.DispatcherQueue,
                ShowMainWindow,
                async () => await ShutdownAsync());

            mainWindow.ShowAppWindow();

            if (state.StartWithWidget)
            {
                widgetWindow.ShowWidget();
            }

            _ = mainWindow.ViewModel.RefreshPreflightAsync();
        }

        public void ShowMainWindow()
        {
            mainWindow?.ShowAppWindow();
        }

        public async Task ShutdownAsync()
        {
            if (isShuttingDown)
            {
                return;
            }

            isShuttingDown = true;

            try
            {
                Host.Services.GetRequiredService<ITrayService>().Dispose();
                await Host.Services.GetRequiredService<AppState>()
                    .PersistAsync(Host.Services.GetRequiredService<ISettingsStore>());
                widgetWindow?.Close();
                mainWindow?.Close();
                await Host.StopAsync();
            }
            finally
            {
                Exit();
            }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            TryWriteStartupLog(e.Exception);
        }

        private static void TryWriteStartupLog(Exception exception)
        {
            try
            {
                var directory = Path.GetDirectoryName(StartupLogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    StartupLogPath,
                    $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}


