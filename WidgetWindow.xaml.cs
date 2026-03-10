using Indolent.Helpers;
using Indolent.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;

namespace Indolent;

public sealed partial class WidgetWindow : Window
{
    private const string OcrAnswerPrompt = "Answer the user's question from this OCR text. OCR may contain noise. If it is multiple choice, return the best option first, then a short reason on the same line. Keep it brief.";
    private const string ScreenshotAnswerPrompt = "Answer the visible question from this screenshot. Use the image as the source of truth, and use any OCR text only as a hint. If it is multiple choice, return the best option first, then a short reason on the same line. Keep it brief.";
    private const double DragThreshold = 3d;

    private readonly AppState appState;
    private readonly ICodexCliService codexCliService;
    private readonly IOcrService ocrService;
    private readonly IScreenCaptureService screenCaptureService;
    private readonly ISettingsStore settingsStore;

    private AppWindow? appWindow;
    private bool wasShowingThinkingText;
    private bool wasShowingMessageText;
    private bool wasShowingActionButton;

    // Drag tracking
    private bool isDragPointerDown;
    private Windows.Foundation.Point dragPressedPosition;
    private uint dragPointerId;

    // Content tracking
    private bool isContentPointerDown;

    public WidgetWindow(WidgetWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        appState = App.CurrentApp.Host.Services.GetRequiredService<AppState>();
        codexCliService = App.CurrentApp.Host.Services.GetRequiredService<ICodexCliService>();
        ocrService = App.CurrentApp.Host.Services.GetRequiredService<IOcrService>();
        screenCaptureService = App.CurrentApp.Host.Services.GetRequiredService<IScreenCaptureService>();
        settingsStore = App.CurrentApp.Host.Services.GetRequiredService<ISettingsStore>();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModel.ShowActionButton)
                or nameof(ViewModel.ShowMessage)
                or nameof(ViewModel.IsBusy))
            {
                UpdateVisualState();
            }
        };
    }

    public WidgetWindowViewModel ViewModel { get; }

    private Storyboard MessageRevealStoryboard
        => (Storyboard)RootGrid.Resources["MessageRevealStoryboard"];

    private Storyboard ThinkingRevealStoryboard
        => (Storyboard)RootGrid.Resources["ThinkingRevealStoryboard"];

    private Storyboard ActionButtonRevealStoryboard
        => (Storyboard)RootGrid.Resources["ActionButtonRevealStoryboard"];

    private Storyboard SubtleBorderGlowStoryboard
        => (Storyboard)RootGrid.Resources["SubtleBorderGlowStoryboard"];

    private Storyboard ThinkingTextShimmerStoryboard
        => (Storyboard)RootGrid.Resources["ThinkingTextShimmerStoryboard"];

    public void InitializeWindow()
    {
        appWindow = this.GetAppWindow();
        appWindow.Title = "Indolent Widget";
        
        var hWnd = this.GetWindowHandle();
        var dpi = NativeMethods.GetDpiForWindow(hWnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        
        var physicalWidth = (int)Math.Round(368 * scale);
        var physicalHeight = (int)Math.Round(72 * scale);

        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
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

        hWnd = this.GetWindowHandle();
        var extendedStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle);
        extendedStyle |= NativeMethods.WsExToolWindow;
        extendedStyle &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetWindowLong(hWnd, NativeMethods.GwlExStyle, extendedStyle);
        
        NativeMethods.SetWindowPos(
            hWnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);

        // Kill the Windows 11 DWM accent border
        var borderColorNone = unchecked((int)NativeMethods.DwmwaColorNone);
        NativeMethods.DwmSetWindowAttribute(hWnd, NativeMethods.DwmwaBorderColor, ref borderColorNone, sizeof(int));

        // Extend glass frame into client area to fully eliminate non-client frame
        var margins = new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(hWnd, ref margins);
        
        SizeChanged += OnSizeChanged;
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

    private async Task TriggerAnswerAsync()
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

    private void OnSurfacePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.SetHovered(true);
        UpdateVisualState();

        if (ViewModel.IsBusy)
        {
            PlayThinkingReveal();
            return;
        }

        if (ViewModel.ShowMessage)
        {
            PlayMessageReveal();
            return;
        }

        if (ViewModel.ShowActionButton)
        {
            PlayActionButtonReveal();
        }
    }

    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.SetHovered(false);
        UpdateVisualState();
    }

    private void OnDragAreaPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(DragHandleArea);
        if (!pointer.Properties.IsLeftButtonPressed) return;

        DragHandleArea.CapturePointer(e.Pointer);
        isDragPointerDown = true;
        dragPointerId = e.Pointer.PointerId;
        dragPressedPosition = pointer.Position;
        e.Handled = true;
    }

    private void OnDragAreaPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragPointerDown || e.Pointer.PointerId != dragPointerId) return;

        var currentPosition = e.GetCurrentPoint(DragHandleArea).Position;
        var deltaX = currentPosition.X - dragPressedPosition.X;
        var deltaY = currentPosition.Y - dragPressedPosition.Y;
        var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (distanceSquared > DragThreshold * DragThreshold)
        {
            DragHandleArea.ReleasePointerCaptures();
            isDragPointerDown = false;
            BeginWindowDrag();
        }
        e.Handled = true;
    }

    private void OnDragAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragPointerDown || e.Pointer.PointerId != dragPointerId) return;

        DragHandleArea.ReleasePointerCaptures();
        isDragPointerDown = false;
        e.Handled = true;
    }

    private void BeginWindowDrag()
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(this.GetWindowHandle(), NativeMethods.WmNclButtonDown, (IntPtr)NativeMethods.HtCaption, IntPtr.Zero);
    }

    private void OnContentAreaPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(ContentArea);
        if (!pointer.Properties.IsLeftButtonPressed) return;

        isContentPointerDown = true;
        e.Handled = true;
    }

    private async void OnContentAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!isContentPointerDown) return;
        isContentPointerDown = false;

        if (IsWithinAnswerButton(e.OriginalSource))
        {
            return;
        }

        if (ViewModel.ShowActionButton && !ViewModel.IsBusy)
        {
            e.Handled = true;
            await TriggerAnswerAsync();
        }
    }

    private async void OnAnswerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        await TriggerAnswerAsync();
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

    private void UpdateVisualState()
    {
        var isThinking = ViewModel.IsBusy;
        var showMessage = ViewModel.ShowMessage && !isThinking;
        var showActionButton = ViewModel.ShowActionButton;

        AnswerButton.Visibility = showActionButton ? Visibility.Visible : Visibility.Collapsed;
        ThinkingTextPresenter.Visibility = isThinking ? Visibility.Visible : Visibility.Collapsed;
        MessageTextBlock.Visibility = showMessage ? Visibility.Visible : Visibility.Collapsed;
        MessageTextBlock.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            ViewModel.IsError ? "WidgetErrorTextBrush" : "WidgetTextBrush"];
        
        UpdateThinkingAnimation(isThinking);
        UpdateThinkingReveal(isThinking);
        UpdateMessageReveal(showMessage);
        UpdateActionButtonReveal(showActionButton);
    }

    private void UpdateThinkingAnimation(bool isThinking)
    {
        if (isThinking)
        {
            ActiveHighlightBorder.Visibility = Visibility.Visible;
            SubtleBorderGlowStoryboard.Begin();
            ThinkingTextShimmerStoryboard.Begin();
        }
        else
        {
            SubtleBorderGlowStoryboard.Stop();
            ThinkingTextShimmerStoryboard.Stop();
            ThinkingTextShimmerTransform.X = -1.25;
            ActiveHighlightBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateThinkingReveal(bool isThinking)
    {
        if (isThinking)
        {
            if (!wasShowingThinkingText) PlayThinkingReveal();
        }
        else
        {
            ThinkingRevealStoryboard.Stop();
            ThinkingTextPresenter.Opacity = 0;
        }
        wasShowingThinkingText = isThinking;
    }

    private void UpdateMessageReveal(bool showMessage)
    {
        if (showMessage)
        {
            if (!wasShowingMessageText) PlayMessageReveal();
        }
        else
        {
            MessageRevealStoryboard.Stop();
            MessageTextBlock.Opacity = 0;
        }
        wasShowingMessageText = showMessage;
    }

    private void UpdateActionButtonReveal(bool showActionButton)
    {
        if (showActionButton)
        {
            if (!wasShowingActionButton) PlayActionButtonReveal();
        }
        else
        {
            ActionButtonRevealStoryboard.Stop();
            AnswerButton.Opacity = 0;
        }
        wasShowingActionButton = showActionButton;
    }

    private void PlayThinkingReveal()
    {
        ThinkingRevealStoryboard.Stop();
        ThinkingTextPresenter.Opacity = 0;
        ThinkingRevealStoryboard.Begin();
    }

    private void PlayMessageReveal()
    {
        MessageRevealStoryboard.Stop();
        MessageTextBlock.Opacity = 0;
        MessageRevealStoryboard.Begin();
    }

    private void PlayActionButtonReveal()
    {
        ActionButtonRevealStoryboard.Stop();
        AnswerButton.Opacity = 0;
        ActionButtonRevealStoryboard.Begin();
    }

    private static bool IsWithinAnswerButton(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button button && button.Name == "AnswerButton")
            {
                return true;
            }
        }

        return false;
    }
}
