using System.Diagnostics;
using Indolent.Helpers;
using Microsoft.UI.Windowing;
using Windows.System;

namespace Indolent;

public sealed partial class MainWindow : Window
{
    private AppWindow? appWindow;

    public MainWindow(MainWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Activated += OnWindowActivated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e)
    {
        var logsDirectory = ViewModel.LogsDirectoryPath;
        Directory.CreateDirectory(logsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logsDirectory}\"") { UseShellExecute = true });
    }

    private void OnToggleTerminalViewClicked(object sender, RoutedEventArgs e)
        => ViewModel.ToggleTerminalView();

    private async void OnRunGuidedSetupClicked(object sender, RoutedEventArgs e)
        => await ViewModel.RunGuidedSetupAsync();

    private async void OnProviderComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await ViewModel.CommitSelectedProviderAsync();

    private async void OnModelComboBoxLostFocus(object sender, RoutedEventArgs e)
        => await ViewModel.CommitSelectedModelAsync();

    private async void OnModelComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await ViewModel.CommitSelectedModelAsync();

    private async void OnReasoningComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await ViewModel.CommitSelectedReasoningAsync();

    private async void OnRunTerminalCommandClicked(object sender, RoutedEventArgs e)
        => await ViewModel.RunTerminalCommandAsync();

    private void OnClearTerminalClicked(object sender, RoutedEventArgs e)
        => ViewModel.ClearTerminalTranscript();

    private async void OnTerminalCommandKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.RunTerminalCommandAsync();
    }

    private void OnOpenInstallGuideClicked(object sender, RoutedEventArgs e)
        => OpenExternal(ViewModel.InstallGuideUrl);

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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.TerminalTranscript))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
                TerminalTranscriptScrollViewer?.ChangeView(null, double.MaxValue, null, disableAnimation: true));
        }
    }

    private static void OpenExternal(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
