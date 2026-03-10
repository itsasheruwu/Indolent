using System.Diagnostics;
using Indolent.Helpers;
using Microsoft.UI.Windowing;

namespace Indolent;

public sealed partial class MainWindow : Window
{
    private AppWindow? appWindow;

    public MainWindow(MainWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Activated += OnWindowActivated;
    }

    public MainWindowViewModel ViewModel { get; }

    public void InitializeWindow()
    {
        appWindow = this.GetAppWindow();
        appWindow.Title = "Indolent";
        appWindow.Resize(new Windows.Graphics.SizeInt32(1040, 760));
        appWindow.Closing += OnAppWindowClosing;
    }

    public void ShowAppWindow()
    {
        this.ShowWin32Window(true);
        Activate();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        => await ViewModel.RefreshPreflightAsync();

    private void OnOpenWidgetClicked(object sender, RoutedEventArgs e)
        => App.CurrentApp.WidgetWindowInstance?.ShowWidget();

    private async void OnModelComboBoxLostFocus(object sender, RoutedEventArgs e)
        => await ViewModel.CommitSelectedModelAsync();

    private async void OnModelComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await ViewModel.CommitSelectedModelAsync();

    private async void OnReasoningComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await ViewModel.CommitSelectedReasoningAsync();

    private void OnOpenInstallGuideClicked(object sender, RoutedEventArgs e)
        => OpenExternal("https://help.openai.com/en/articles/11096431-openai-codex-cli-getting-started");

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.CurrentApp.IsShuttingDown)
        {
            return;
        }

        args.Cancel = true;
        this.HideWindow();
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            await ViewModel.RefreshPreflightAsync();
        }
    }

    private static void OpenExternal(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
