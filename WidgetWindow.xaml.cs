using Indolent.Helpers;
using Indolent.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;

namespace Indolent;

public sealed partial class WidgetWindow : Window
{
    private const string OcrAnswerPrompt = "Answer the user's question from this OCR text. OCR may contain noise. If it is multiple choice, return the best option first, then a short reason on the same line. Keep it brief.";
    private const string ScreenshotAnswerPrompt = "Answer the visible question from this screenshot. Use the image as the source of truth, and use any OCR text only as a hint. If it is multiple choice, return the best option first, then a short reason on the same line. Keep it brief.";
    private const double ThinkingSweepStartX = -140d;

    private readonly AppState appState;
    private readonly ICodexCliService codexCliService;
    private readonly IOcrService ocrService;
    private readonly IScreenCaptureService screenCaptureService;
    private readonly ISettingsStore settingsStore;

    private AppWindow? appWindow;
    private bool isThinkingAnimationActive;

    public WidgetWindow(WidgetWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        appState = App.CurrentApp.Host.Services.GetRequiredService<AppState>();
        codexCliService = App.CurrentApp.Host.Services.GetRequiredService<ICodexCliService>();
        ocrService = App.CurrentApp.Host.Services.GetRequiredService<IOcrService>();
        screenCaptureService = App.CurrentApp.Host.Services.GetRequiredService<IScreenCaptureService>();
        settingsStore = App.CurrentApp.Host.Services.GetRequiredService<ISettingsStore>();
        ToolTipService.SetToolTip(RootGrid, ViewModel.TooltipText);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModel.ShowActionButton)
                or nameof(ViewModel.ShowMessage)
                or nameof(ViewModel.TooltipText)
                or nameof(ViewModel.IsBusy))
            {
                UpdateVisualState();
            }
        };
    }

    public WidgetWindowViewModel ViewModel { get; }

    private Storyboard ThinkingSweepStoryboard
        => (Storyboard)RootGrid.Resources["ThinkingSweepStoryboard"];

    public void InitializeWindow()
    {
        appWindow = this.GetAppWindow();
        appWindow.Title = "Indolent Widget";
        appWindow.Resize(new Windows.Graphics.SizeInt32((int)appState.WidgetBounds.Width, (int)appState.WidgetBounds.Height));
        appWindow.Move(new Windows.Graphics.PointInt32((int)appState.WidgetBounds.X, (int)appState.WidgetBounds.Y));
        appWindow.Changed += OnAppWindowChanged;
        appWindow.Closing += OnAppWindowClosing;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        var hWnd = this.GetWindowHandle();
        var extendedStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle);
        extendedStyle |= NativeMethods.WsExToolWindow;
        extendedStyle &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetWindowLong(hWnd, NativeMethods.GwlExStyle, extendedStyle);
        ApplyRoundedRegion();
        NativeMethods.SetWindowPos(
            hWnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        SizeChanged += OnSizeChanged;
        Activated += (_, _) => ApplyRoundedRegion();
        UpdateVisualState();
    }

    public void ShowWidget()
    {
        this.ShowWin32Window(true);
        Activate();
        BringToFront();
    }

    public void BringToFront()
    {
        NativeMethods.SetWindowPos(
            this.GetWindowHandle(),
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoActivate);
    }

    private async void OnAnswerClicked(object sender, RoutedEventArgs e)
    {
        if (!appState.CanAnswer)
        {
            return;
        }

        appState.BeginAnswer();
        ViewModel.SetThinking();
        UpdateVisualState();

        string? capturePath = null;

        try
        {
            this.HideWindow();
            await Task.Delay(120);
            capturePath = await screenCaptureService.CaptureDisplayUnderCursorAsync();
            this.ShowWin32Window(false);
            BringToFront();
            await Task.Delay(50);
            var screenText = await ocrService.ExtractTextAsync(capturePath);
            var result = await GetBestAnswerAsync(capturePath, screenText);

            appState.CompleteAnswer(result);
            ViewModel.SetAnswerResult(result);
        }
        catch (Exception ex)
        {
            var result = new AnswerResult
            {
                Status = AnswerStatus.Failed,
                ErrorMessage = $"Answer failed: {ex.Message}"
            };

            appState.CompleteAnswer(result);
            ViewModel.SetAnswerResult(result);
        }
        finally
        {
            this.ShowWin32Window(false);
            BringToFront();
            UpdateVisualState();

            if (!string.IsNullOrWhiteSpace(capturePath) && File.Exists(capturePath))
            {
                try
                {
                    File.Delete(capturePath);
                }
                catch
                {
                    // ignore best-effort cleanup
                }
            }

            await appState.PersistAsync(settingsStore);
        }
    }

    private async Task<AnswerResult> GetBestAnswerAsync(string capturePath, string screenText)
    {
        if (string.IsNullOrWhiteSpace(screenText))
        {
            return await AnswerFromScreenshotAsync(capturePath, screenText);
        }

        var ocrResult = await codexCliService.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            Prompt = OcrAnswerPrompt,
            ReasoningEffort = appState.SelectedReasoningEffort,
            RequestedAt = DateTimeOffset.Now
        });

        return NeedsScreenshotFallback(ocrResult)
            ? await AnswerFromScreenshotAsync(capturePath, screenText)
            : ocrResult;
    }

    private Task<AnswerResult> AnswerFromScreenshotAsync(string capturePath, string screenText)
        => codexCliService.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            ScreenshotPath = capturePath,
            Prompt = ScreenshotAnswerPrompt,
            ReasoningEffort = appState.SelectedReasoningEffort,
            RequestedAt = DateTimeOffset.Now
        });

    private static bool NeedsScreenshotFallback(AnswerResult result)
    {
        if (!result.IsSuccess)
        {
            return false;
        }

        var text = result.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Contains("ocr alone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("text alone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot determine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("can't determine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable to determine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("need the screenshot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("need the image", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not enough context", StringComparison.OrdinalIgnoreCase);
    }

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.SetHovered(true);
        UpdateVisualState();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.SetHovered(false);
        UpdateVisualState();
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsBusy || e.OriginalSource is Button)
        {
            return;
        }

        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(this.GetWindowHandle(), NativeMethods.WmNclButtonDown, (IntPtr)NativeMethods.HtCaption, IntPtr.Zero);
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.CurrentApp.IsShuttingDown)
        {
            return;
        }

        args.Cancel = true;
        this.HideWindow();
        await appState.PersistAsync(settingsStore);
    }

    private async void OnSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        ApplyRoundedRegion();

        var position = appWindow?.Position ?? new Windows.Graphics.PointInt32((int)appState.WidgetBounds.X, (int)appState.WidgetBounds.Y);
        appState.UpdateWidgetBounds(position.X, position.Y, args.Size.Width, args.Size.Height);
        await appState.PersistAsync(settingsStore);
    }

    private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        var position = sender.Position;
        appState.UpdateWidgetBounds(position.X, position.Y, size.Width, size.Height);
        await appState.PersistAsync(settingsStore);
    }

    private void ApplyRoundedRegion()
    {
        var region = NativeMethods.CreateRoundRectRgn(0, 0, (int)RootGrid.Width, (int)RootGrid.Height, 112, 112);
        NativeMethods.SetWindowRgn(this.GetWindowHandle(), region, true);
    }

    private void UpdateVisualState()
    {
        var isThinking = ViewModel.IsBusy;

        AnswerButton.Visibility = ViewModel.ShowActionButton ? Visibility.Visible : Visibility.Collapsed;
        ThinkingTextPresenter.Visibility = isThinking ? Visibility.Visible : Visibility.Collapsed;
        MessageTextBlock.Visibility = ViewModel.ShowMessage && !isThinking ? Visibility.Visible : Visibility.Collapsed;
        MessageTextBlock.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            ViewModel.IsError ? "WidgetErrorTextBrush" : "WidgetTextBrush"];
        UpdateThinkingAnimation(isThinking);
        ToolTipService.SetToolTip(RootGrid, ViewModel.TooltipText);
    }

    private void UpdateThinkingAnimation(bool isThinking)
    {
        if (isThinking)
        {
            if (isThinkingAnimationActive)
            {
                return;
            }

            ResetThinkingSweep();
            ThinkingSweepStoryboard.Begin();
            isThinkingAnimationActive = true;
            return;
        }

        if (!isThinkingAnimationActive)
        {
            return;
        }

        ThinkingSweepStoryboard.Stop();
        ResetThinkingSweep();
        isThinkingAnimationActive = false;
    }

    private void ResetThinkingSweep()
        => ThinkingSweepTransform.X = ThinkingSweepStartX;
}
