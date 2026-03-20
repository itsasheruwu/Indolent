using Indolent.Helpers;
using Indolent.Services;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;

namespace Indolent;

public sealed partial class WidgetWindow : Window
{
    private const double ThinkingTextShimmerStartX = -1.35d;
    private static readonly string[] ReasoningEscalationOrder = ["minimal", "low", "medium", "high", "xhigh"];
    private const int AgentLoopDelayMilliseconds = 450;
    private const int MinVideoStandbyPollMilliseconds = 40000;
    private const int MaxVideoStandbyPollMilliseconds = 65000;
    private const int MinKnownVideoStandbyPollMilliseconds = 1000;
    private const int KnownVideoCompletionBufferMilliseconds = 750;
    private const int MaxKnownVideoStandbyPollMilliseconds = 10 * 60 * 1000;
    private const int LoopTransitionPollMilliseconds = 900;
    private const int MaxLoopTransitionPolls = 7;
    private const int MaxAgentLoopQuestions = 50;
    private const double VideoSettingsGearRelativeX = 0.723;
    private const double VideoSettingsGearRelativeY = 0.808;
    private const double VideoSpeedMenuRelativeX = 0.694;
    private const double VideoSpeedMenuRelativeY = 0.734;
    private const double VideoSpeedOnePointFiveRelativeX = 0.694;
    private const double VideoSpeedOnePointFiveRelativeY = 0.764;
    private const double VideoPlaybackSpeedMultiplier = 1.5d;
    private const string OcrAnswerPrompt = "Answer the user's question from this OCR text. OCR may contain noise. If it is multiple choice, return only the best option unless a short clarification is necessary. Keep it brief.";
    private const string ScreenshotAnswerPrompt = "Answer the visible question from this screenshot. Use the image as the source of truth, and use any OCR text only as a hint. If it is multiple choice, return only the best option unless a short clarification is necessary. Keep it brief.";
    private const string AgentOcrAnswerPrompt = "Answer the user's question from this OCR text. OCR may contain noise. Return only the exact answer text or option label as shown on screen. No explanation.";
    private const string AgentScreenshotAnswerPrompt = "Answer the visible question from this screenshot. Use the image as the source of truth and OCR only as a hint. Return only the exact answer text or option label as shown on screen. No explanation.";
    private const string VideoDetectionPrompt = "Check whether this screenshot is a video/player state rather than a question screen. Return only VIDEO or NOT_VIDEO. Return VIDEO if you can see player controls such as play/pause, settings/gear, rewind/reverse, progress UI, or a title/interstitial without a visible question.";
    private readonly AppState appState;
    private readonly IAgentClickService agentClickService;
    private readonly IOcrService ocrService;
    private readonly IProviderRuntimeRegistry providerRegistry;
    private readonly IScreenCaptureService screenCaptureService;
    private readonly ISettingsStore settingsStore;
    private readonly Random random = new();
    private CancellationTokenSource? activeAnswerCancellationSource;

    private AppWindow? appWindow;
    private bool wasShowingThinkingText;
    private bool wasShowingMessageText;
    private bool wasShowingActionButton;
    private string? activeVideoSignature;
    private TimeSpan? activeVideoTotalDuration;
    private bool hasHandledCurrentVideoSpeed;

    // Drag tracking
    private bool isDragging;
    private Windows.Graphics.PointInt32 dragStartWindowPosition;
    private NativeMethods.NativePoint dragStartCursorPosition;

    // Content tracking
    private bool isContentPointerDown;

    public WidgetWindow(WidgetWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        appState = App.CurrentApp.Host.Services.GetRequiredService<AppState>();
        agentClickService = App.CurrentApp.Host.Services.GetRequiredService<IAgentClickService>();
        ocrService = App.CurrentApp.Host.Services.GetRequiredService<IOcrService>();
        providerRegistry = App.CurrentApp.Host.Services.GetRequiredService<IProviderRuntimeRegistry>();
        screenCaptureService = App.CurrentApp.Host.Services.GetRequiredService<IScreenCaptureService>();
        settingsStore = App.CurrentApp.Host.Services.GetRequiredService<ISettingsStore>();

        ViewModel.PropertyChanged += (_, e) =>
        {
            UpdateVisualState();
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

    private IProviderRuntime CurrentProviderRuntime => providerRegistry.GetProvider(appState.SelectedProviderId);

    private bool IsOpenCodeProvider
        => string.Equals(CurrentProviderRuntime.ProviderId, ProviderIds.OpenCode, StringComparison.OrdinalIgnoreCase);

    public void InitializeWindow()
    {
        appWindow = this.GetAppWindow();
        appWindow.Title = "Indolent Widget";
        
        var hWnd = this.GetWindowHandle();
        var dpi = NativeMethods.GetDpiForWindow(hWnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        
        var physicalWidth = (int)Math.Round(368 * scale);
        var physicalHeight = (int)Math.Round(72 * scale);

        var initialPosition = GetVisibleWidgetPosition(physicalWidth, physicalHeight);
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        appWindow.Move(initialPosition);
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

        using var answerCancellationSource = new CancellationTokenSource();
        activeAnswerCancellationSource = answerCancellationSource;

        appState.BeginAnswer();
        ViewModel.SetThinking();
        UpdateVisualState();

        try
        {
            var result = appState.AgentModeEnabled && appState.AgentLoopEnabled
                ? await RunAgentLoopAsync(answerCancellationSource.Token)
                : await RunSingleAnswerAsync(answerCancellationSource.Token);

            appState.CompleteAnswer(result);
            ViewModel.SetAnswerResult(result);
        }
        catch (OperationCanceledException)
        {
            appState.CancelAnswer();
            UpdateVisualState();
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
            if (ReferenceEquals(activeAnswerCancellationSource, answerCancellationSource))
            {
                activeAnswerCancellationSource = null;
            }

            this.ShowWin32Window(false);
            BringToFront();
            UpdateVisualState();

            await appState.PersistAsync(settingsStore);
        }
    }

    private void CancelActiveAnswer()
    {
        activeAnswerCancellationSource?.Cancel();
    }

    private async Task<AnswerResult> RunSingleAnswerAsync(CancellationToken cancellationToken)
    {
        var iteration = await ExecuteAnswerIterationAsync(showCaptureStatus: true, showOcrStatus: true, cancellationToken: cancellationToken);
        return iteration.Result;
    }

    private async Task<AnswerResult> RunAgentLoopAsync(CancellationToken cancellationToken)
    {
        var answeredQuestions = 0;
        var isFirstIteration = true;

        while (answeredQuestions < MaxAgentLoopQuestions)
        {
            CaptureLayoutResult? initialCapture = null;
            if (isFirstIteration)
            {
                initialCapture = await CaptureCurrentLayoutUnderCursorAsync(showCaptureStatus: true, showOcrStatus: true, cancellationToken: cancellationToken);
            }

            var readySnapshot = await WaitForVideoToFinishAsync(
                initialCapture?.Layout,
                showCaptureStatus: !isFirstIteration,
                showOcrStatus: true,
                cancellationToken: cancellationToken);

            var currentReasoning = appState.SelectedReasoningEffort;
            AnswerIterationResult? iteration = null;
            OcrLayoutResult? followUpOcr = null;
            var canReuseInitialCapture = initialCapture is not null
                && ReferenceEquals(readySnapshot, initialCapture.Layout)
                && IsLikelyQuestionScreen(initialCapture.Layout)
                && !ContainsVideoSignal(initialCapture.Layout)
                && !ContainsResultsSummarySignal(initialCapture.Layout.Text);

            try
            {
                while (true)
                {
                    iteration = canReuseInitialCapture
                        ? await ExecuteAnswerIterationAsync(
                            currentReasoning,
                            captureOverride: initialCapture!.Capture,
                            layoutOverride: initialCapture.Layout,
                            showCaptureStatus: false,
                            showOcrStatus: false,
                            cancellationToken: cancellationToken)
                        : await ExecuteAnswerIterationAsync(
                            currentReasoning,
                            showCaptureStatus: false,
                            showOcrStatus: true,
                            cancellationToken: cancellationToken);
                    if (!iteration.Result.IsSuccess || !iteration.ClickResult.Clicked)
                    {
                        return CreateLoopFinishedResult(answeredQuestions);
                    }

                    var questionSignature = BuildLoopSignature(iteration.OcrLayout.Text);
                    followUpOcr = await WaitForPostClickSnapshotAsync(questionSignature, cancellationToken);

                    if (!ContainsSkipSignal(followUpOcr.Text))
                    {
                        break;
                    }

                    if (!TryGetMoreReasoning(currentReasoning, out var strongerReasoning))
                    {
                        return CreateLoopFinishedResult(answeredQuestions);
                    }

                    currentReasoning = strongerReasoning;
                    canReuseInitialCapture = false;
                }
            }
            finally
            {
                if (initialCapture is not null)
                {
                    TryDeleteCapture(initialCapture.Capture.ImagePath);
                }
            }

            if (iteration is null || followUpOcr is null)
            {
                return CreateLoopFinishedResult(answeredQuestions);
            }

            if (ContainsResultsSummarySignal(followUpOcr.Text))
            {
                if (!await TryAdvanceResultsSummaryAsync(followUpOcr, cancellationToken))
                {
                    return CreateLoopFinishedResult(answeredQuestions);
                }

                followUpOcr = await WaitForPostClickSnapshotAsync(BuildLoopSignature(followUpOcr.Text), cancellationToken);
            }

            var currentSignature = BuildLoopSignature(followUpOcr.Text);
            if (string.IsNullOrWhiteSpace(currentSignature)
                || string.Equals(currentSignature, BuildLoopSignature(iteration.OcrLayout.Text), StringComparison.Ordinal))
            {
                return CreateLoopFinishedResult(answeredQuestions);
            }

            answeredQuestions++;
            isFirstIteration = false;
        }

        return CreateLoopFinishedResult(answeredQuestions);
    }

    private async Task<AnswerIterationResult> ExecuteAnswerIterationAsync(
        string? reasoningEffortOverride = null,
        ScreenCaptureResult? captureOverride = null,
        OcrLayoutResult? layoutOverride = null,
        bool showCaptureStatus = true,
        bool showOcrStatus = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capture = captureOverride ?? await CaptureDisplayUnderCursorWithStatusAsync(showCaptureStatus, cancellationToken: cancellationToken);
        var ownsCapture = captureOverride is null;

        try
        {
            var ocrLayout = layoutOverride ?? await ExtractLayoutWithStatusAsync(capture.ImagePath, showOcrStatus, cancellationToken: cancellationToken);
            var reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffortOverride)
                ? appState.SelectedReasoningEffort
                : reasoningEffortOverride;
            var result = await GetBestAnswerAsync(capture.ImagePath, ocrLayout.Text, appState.AgentModeEnabled, reasoningEffort, cancellationToken);
            var clickResult = new AgentClickResult();

            if (appState.AgentModeEnabled && result.IsSuccess)
            {
                this.HideWindow();
                clickResult = await agentClickService.TryClickAnswerAsync(
                    result.Text,
                    capture,
                    ocrLayout,
                    appState.SelectedModel,
                    reasoningEffort,
                    cancellationToken: cancellationToken);
                this.ShowWin32Window(false);
                BringToFront();
            }

            return new AnswerIterationResult(result, clickResult, ocrLayout);
        }
        finally
        {
            if (ownsCapture)
            {
                TryDeleteCapture(capture.ImagePath);
            }
        }
    }

    private async Task<OcrLayoutResult> CaptureOcrLayoutSnapshotAsync(bool showCaptureStatus = true, bool showOcrStatus = true, CancellationToken cancellationToken = default)
    {
        var capture = await CaptureDisplayUnderCursorWithStatusAsync(showCaptureStatus, cancellationToken: cancellationToken);

        try
        {
            return await ExtractLayoutWithStatusAsync(capture.ImagePath, showOcrStatus, cancellationToken: cancellationToken);
        }
        finally
        {
            TryDeleteCapture(capture.ImagePath);
        }
    }

    private async Task<OcrLayoutResult> WaitForVideoToFinishAsync(
        OcrLayoutResult? initialSnapshot = null,
        bool showCaptureStatus = true,
        bool showOcrStatus = true,
        CancellationToken cancellationToken = default)
    {
        var snapshot = initialSnapshot ?? await CaptureOcrLayoutSnapshotAsync(showCaptureStatus, showOcrStatus, cancellationToken: cancellationToken);
        if (ContainsResultsSummarySignal(snapshot.Text))
        {
            return snapshot;
        }

        var videoDetected = false;
        if (!IsLikelyQuestionScreen(snapshot))
        {
            var probe = await CaptureCenteredHoverSnapshotAsync(showCaptureStatus, showOcrStatus, cancellationToken: cancellationToken);
            snapshot = probe.OcrLayout;
            videoDetected = probe.VideoDetected;
        }

        while (!ContainsResultsSummarySignal(snapshot.Text) && (videoDetected || ContainsVideoSignal(snapshot)))
        {
            var remaining = GetRemainingVideoDuration(snapshot, hasHandledCurrentVideoSpeed);
            await WaitWithVideoCountdownAsync(remaining, GetNextVideoStandbyDelayMilliseconds(remaining), cancellationToken: cancellationToken);
            var probe = await CaptureCenteredHoverSnapshotAsync(showCaptureStatus, showOcrStatus, cancellationToken: cancellationToken);
            snapshot = probe.OcrLayout;
            videoDetected = probe.VideoDetected;
        }

        ViewModel.SetThinking();
        UpdateVisualState();
        ResetVideoPlaybackTracking();
        return snapshot;
    }

    private async Task<OcrLayoutResult> WaitForPostClickSnapshotAsync(string questionSignature, CancellationToken cancellationToken)
    {
        OcrLayoutResult? lastSnapshot = null;

        for (var attempt = 0; attempt < MaxLoopTransitionPolls; attempt++)
        {
            await Task.Delay(attempt == 0 ? AgentLoopDelayMilliseconds : LoopTransitionPollMilliseconds, cancellationToken);
            lastSnapshot = await CaptureOcrLayoutSnapshotAsync(showCaptureStatus: false, showOcrStatus: true, cancellationToken: cancellationToken);
            lastSnapshot = await WaitForVideoToFinishAsync(lastSnapshot, showCaptureStatus: false, showOcrStatus: true, cancellationToken: cancellationToken);

            if (ContainsSkipSignal(lastSnapshot.Text))
            {
                return lastSnapshot;
            }

            var currentSignature = BuildLoopSignature(lastSnapshot.Text);
            if (!string.IsNullOrWhiteSpace(currentSignature)
                && !string.Equals(currentSignature, questionSignature, StringComparison.Ordinal))
            {
                return lastSnapshot;
            }
        }

        return lastSnapshot ?? new OcrLayoutResult();
    }

    private async Task<VideoProbeResult> CaptureCenteredHoverSnapshotAsync(bool showCaptureStatus = true, bool showOcrStatus = true, CancellationToken cancellationToken = default)
    {
        NativeMethods.GetCursorPos(out var currentCursor);
        var monitor = NativeMethods.MonitorFromPoint(currentCursor, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new VideoProbeResult(await CaptureOcrLayoutSnapshotAsync(showCaptureStatus, showOcrStatus, cancellationToken: cancellationToken), false);
        }

        var centerX = monitorInfo.Monitor.Left + ((monitorInfo.Monitor.Right - monitorInfo.Monitor.Left) / 2);
        var centerY = monitorInfo.Monitor.Top + ((monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top) / 2);
        var probeCapture = await CaptureAtPointWithStatusAsync(centerX, centerY, clickCenter: false, showCaptureStatus: showCaptureStatus, cancellationToken: cancellationToken);
        try
        {
            var snapshot = await ExtractLayoutWithStatusAsync(probeCapture.ImagePath, showOcrStatus, cancellationToken: cancellationToken);
            var videoDetected = ContainsVideoSignal(snapshot) || await DetectVideoStateFromScreenshotAsync(probeCapture.ImagePath, snapshot.Text, cancellationToken: cancellationToken);
            if (videoDetected)
            {
                if (!HasVisibleVideoPlaybackUi(snapshot))
                {
                    await TryClickAbsolutePointAsync(centerX, centerY, cancellationToken);
                    await Task.Delay(260, cancellationToken);
                    var revealedCapture = await CaptureAtPointWithStatusAsync(centerX, centerY, clickCenter: false, showStatus: false, cancellationToken: cancellationToken);
                    try
                    {
                        snapshot = await ExtractLayoutWithStatusAsync(revealedCapture.ImagePath, showStatus: false, cancellationToken: cancellationToken);
                    }
                    finally
                    {
                        TryDeleteCapture(revealedCapture.ImagePath);
                    }
                }

                var handledVideoInteraction = await EnsureCurrentVideoPlaybackConfiguredAsync(probeCapture, snapshot, cancellationToken: cancellationToken);
                if (handledVideoInteraction)
                {
                    this.HideWindow();
                    ClickAbsolutePoint(centerX, centerY);
                    await Task.Delay(220, cancellationToken);
                    this.ShowWin32Window(false);
                    BringToFront();
                }
            }

            return new VideoProbeResult(snapshot, videoDetected);
        }
        finally
        {
            TryDeleteCapture(probeCapture.ImagePath);
        }
    }

    private async Task<ScreenCaptureResult> CaptureAtPointAsync(int x, int y, bool clickCenter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeMethods.SetCursorPos(x, y);
        await Task.Delay(180, cancellationToken);
        if (clickCenter)
        {
            ClickAbsolutePoint(x, y);
            await Task.Delay(320, cancellationToken);
        }

        return await screenCaptureService.CaptureDisplayUnderCursorAsync(cancellationToken);
    }

    private async Task<bool> TryAdvanceResultsSummaryAsync(OcrLayoutResult currentLayout, CancellationToken cancellationToken)
    {
        if (!ContainsResultsSummarySignal(currentLayout.Text))
        {
            return false;
        }

        var capture = await CaptureDisplayUnderCursorWithStatusAsync(showStatus: false, cancellationToken: cancellationToken);

        try
        {
            var ocrLayout = await ExtractLayoutWithStatusAsync(capture.ImagePath, showStatus: true, cancellationToken: cancellationToken);
            if (!ContainsResultsSummarySignal(ocrLayout.Text))
            {
                return false;
            }

            this.HideWindow();
            var clickResult = await agentClickService.TryClickAnswerAsync(
                "continue",
                capture,
                ocrLayout,
                appState.SelectedModel,
                appState.SelectedReasoningEffort,
                cancellationToken: cancellationToken);
            this.ShowWin32Window(false);
            BringToFront();
            return clickResult.Clicked;
        }
        finally
        {
            TryDeleteCapture(capture.ImagePath);
        }
    }

    private async Task<AnswerResult> GetBestAnswerAsync(string capturePath, string screenText, bool agentModeEnabled, string reasoningEffort, CancellationToken cancellationToken)
    {
        if (IsOpenCodeProvider && string.IsNullOrWhiteSpace(screenText))
        {
            return new AnswerResult
            {
                Status = AnswerStatus.Failed,
                ErrorMessage = "Open Code can only answer when local OCR extracts readable question text."
            };
        }

        if (string.IsNullOrWhiteSpace(screenText))
        {
            return await AnswerFromScreenshotAsync(capturePath, screenText, agentModeEnabled, reasoningEffort, cancellationToken);
        }

        var ocrResult = await CurrentProviderRuntime.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            Prompt = agentModeEnabled ? AgentOcrAnswerPrompt : OcrAnswerPrompt,
            ReasoningEffort = reasoningEffort,
            RequestedAt = DateTimeOffset.Now
        }, cancellationToken);

        if (!NeedsScreenshotFallback(ocrResult))
        {
            return ocrResult;
        }

        if (IsOpenCodeProvider)
        {
            return new AnswerResult
            {
                Status = AnswerStatus.Failed,
                ErrorMessage = "Open Code could not answer from OCR alone. Screenshot-dependent fallback is disabled for this provider."
            };
        }

        return await AnswerFromScreenshotAsync(capturePath, screenText, agentModeEnabled, reasoningEffort, cancellationToken);
    }

    private async Task<bool> DetectVideoStateFromScreenshotAsync(string capturePath, string screenText, CancellationToken cancellationToken)
    {
        if (IsOpenCodeProvider)
        {
            return ContainsVideoPhrase(screenText)
                || ContainsVideoControl(screenText)
                || VideoTimecodeRegex().IsMatch(screenText);
        }

        var result = await CurrentProviderRuntime.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            ScreenshotPath = capturePath,
            Prompt = VideoDetectionPrompt,
            ReasoningEffort = "low",
            RequestedAt = DateTimeOffset.Now
        }, cancellationToken);

        return result.IsSuccess
            && result.Text.Contains("VIDEO", StringComparison.OrdinalIgnoreCase)
            && !result.Text.Contains("NOT_VIDEO", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> EnsureCurrentVideoPlaybackConfiguredAsync(ScreenCaptureResult capture, OcrLayoutResult layout, CancellationToken cancellationToken)
    {
        if (TryExtractVideoProgress(layout, out var progress)
            && progress.Current >= progress.Total.Subtract(TimeSpan.FromSeconds(1)))
        {
            ResetVideoPlaybackTracking();
            return false;
        }

        var videoSignature = BuildVideoSignature(layout);
        var totalDuration = TryExtractVideoProgress(layout, out progress) ? progress.Total : (TimeSpan?)null;
        var isSameTrackedVideo = string.Equals(activeVideoSignature, videoSignature, StringComparison.Ordinal)
            && Nullable.Equals(activeVideoTotalDuration, totalDuration);

        if (!isSameTrackedVideo)
        {
            activeVideoSignature = videoSignature;
            activeVideoTotalDuration = totalDuration;
            hasHandledCurrentVideoSpeed = false;
        }

        if (hasHandledCurrentVideoSpeed)
        {
            return false;
        }

        hasHandledCurrentVideoSpeed = true;
        await TryConfigureVideoPlaybackSpeedAsync(capture, layout, cancellationToken: cancellationToken);
        return true;
    }

    private async Task TryConfigureVideoPlaybackSpeedAsync(ScreenCaptureResult capture, OcrLayoutResult layout, CancellationToken cancellationToken)
    {
        if (!await TryOpenVideoSettingsMenuAsync(capture.Bounds, cancellationToken: cancellationToken))
        {
            return;
        }

        await Task.Delay(300, cancellationToken);
        if (!await TryClickVideoMenuEntryAsync(
                ["speed", "playback speed"],
                capture.Bounds,
                fallbackClick: () => TryClickVideoSpeedMenuAsync(capture.Bounds, cancellationToken),
                cancellationToken: cancellationToken))
        {
            return;
        }

        await Task.Delay(300, cancellationToken);
        await TryClickVideoMenuEntryAsync(
            ["1.5x", "1.5 x", "1 5x", "1 5 x", "1.50x", "1.50 x"],
            capture.Bounds,
            fallbackClick: () => TryClickVideoOnePointFiveSpeedAsync(capture.Bounds, cancellationToken),
            cancellationToken: cancellationToken);
    }

    private async Task<bool> TryOpenVideoSettingsMenuAsync(Rectangle captureBounds, CancellationToken cancellationToken)
    {
        foreach (var point in GetVideoSettingsGearClickCandidates(captureBounds))
        {
            if (!await TryClickAbsolutePointAsync(point.X, point.Y, cancellationToken: cancellationToken))
            {
                continue;
            }

            await Task.Delay(260, cancellationToken);
            if (await IsVideoSettingsMenuOpenAsync(cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryClickVideoSettingsGearAsync(Rectangle captureBounds, CancellationToken cancellationToken)
    {
        var point = GetVideoSettingsGearPoint(captureBounds);
        return await TryClickAbsolutePointAsync(point.X, point.Y, cancellationToken: cancellationToken);
    }

    private async Task<bool> TryClickVideoMenuEntryAsync(
        IReadOnlyList<string> targetPhrases,
        Rectangle fallbackCaptureBounds,
        Func<Task<bool>> fallbackClick,
        CancellationToken cancellationToken)
    {
        CaptureLayoutResult? snapshot = null;

        try
        {
            snapshot = await CaptureCurrentLayoutUnderCursorAsync(showCaptureStatus: false, showOcrStatus: false, cancellationToken: cancellationToken);
            if (TryFindMatchingRegion(snapshot.Layout, targetPhrases, out var region))
            {
                ClickRegion(snapshot.Capture.Bounds, region.Bounds);
                await Task.Delay(220, cancellationToken);
                return true;
            }
        }
        finally
        {
            if (snapshot is not null)
            {
                TryDeleteCapture(snapshot.Capture.ImagePath);
            }
        }

        return await fallbackClick();
    }

    private async Task<bool> TryClickVideoSpeedMenuAsync(Rectangle captureBounds, CancellationToken cancellationToken)
    {
        var point = GetVideoSpeedMenuPoint(captureBounds);
        return await TryClickAbsolutePointAsync(point.X, point.Y, cancellationToken: cancellationToken);
    }

    private async Task<bool> TryClickVideoOnePointFiveSpeedAsync(Rectangle captureBounds, CancellationToken cancellationToken)
    {
        var point = GetVideoOnePointFiveSpeedPoint(captureBounds);
        return await TryClickAbsolutePointAsync(point.X, point.Y, cancellationToken: cancellationToken);
    }

    private async Task<bool> TryClickAbsolutePointAsync(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.HideWindow();
        ClickAbsolutePoint(x, y);
        await Task.Delay(220, cancellationToken);
        this.ShowWin32Window(false);
        BringToFront();
        return true;
    }

    private async Task<CaptureLayoutResult> CaptureCurrentLayoutUnderCursorAsync(bool showCaptureStatus = true, bool showOcrStatus = true, CancellationToken cancellationToken = default)
    {
        var capture = await CaptureDisplayUnderCursorWithStatusAsync(showCaptureStatus, cancellationToken: cancellationToken);
        var layout = await ExtractLayoutWithStatusAsync(capture.ImagePath, showOcrStatus, cancellationToken: cancellationToken);
        return new CaptureLayoutResult(capture, layout);
    }

    private Task<AnswerResult> AnswerFromScreenshotAsync(string capturePath, string screenText, bool agentModeEnabled, string reasoningEffort, CancellationToken cancellationToken)
        => CurrentProviderRuntime.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            ScreenshotPath = capturePath,
            Prompt = agentModeEnabled ? AgentScreenshotAnswerPrompt : ScreenshotAnswerPrompt,
            ReasoningEffort = reasoningEffort,
            RequestedAt = DateTimeOffset.Now
        }, cancellationToken);

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
            || text.Contains("not visible", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || text.Contains("need the screenshot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("need the image", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not enough context", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLoopSignature(string text)
        => string.Join(
                ' ',
                text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(64))
            .Trim()
            .ToLowerInvariant()
            .Normalize();

    private static bool TryFindMatchingRegion(
        OcrLayoutResult layout,
        IReadOnlyList<string> targetPhrases,
        out OcrTextRegion region)
    {
        var normalizedTargets = targetPhrases
            .Select(NormalizeForMatching)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .ToArray();

        var regions = layout.Lines.Count > 0
            ? layout.Lines.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            : layout.Words.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text));

        region = regions
            .Select(candidate => new
            {
                Region = candidate,
                Normalized = NormalizeForMatching(candidate.Text),
                Score = normalizedTargets.Max(target => ScoreMenuCandidate(candidate.Text, NormalizeForMatching(candidate.Text), target))
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Region.Bounds.Top)
            .ThenBy(candidate => candidate.Region.Bounds.Left)
            .Select(candidate => candidate.Region)
            .FirstOrDefault() ?? new OcrTextRegion();

        return !string.IsNullOrWhiteSpace(region.Text);
    }

    private static string NormalizeForMatching(string value)
        => string.Join(
            ' ',
            value.ToLowerInvariant()
                .Replace('×', 'x')
                .Split(['\r', '\n', '\t', ' ', '.', ',', ':', ';', '-', '_', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static double GetTokenOverlapScore(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        var shared = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        return (double)shared / Math.Max(leftTokens.Length, rightTokens.Length);
    }

    private static double ScoreMenuCandidate(string rawCandidate, string normalizedCandidate, string normalizedTarget)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return 0;
        }

        if (ContainsForbiddenMenuMismatch(normalizedCandidate, normalizedTarget))
        {
            return 0;
        }

        if (string.Equals(normalizedCandidate, normalizedTarget, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (normalizedCandidate.StartsWith(normalizedTarget + " ", StringComparison.Ordinal))
        {
            return 0.98;
        }

        if (normalizedCandidate.Contains(" " + normalizedTarget + " ", StringComparison.Ordinal))
        {
            return 0.95;
        }

        if (normalizedTarget.Contains('x'))
        {
            return ContainsExactSpeedLabel(rawCandidate, normalizedTarget) ? 0.96 : 0;
        }

        var overlap = GetTokenOverlapScore(normalizedCandidate, normalizedTarget);
        return overlap >= 0.999 ? 0.9 : 0;
    }

    private static bool ContainsForbiddenMenuMismatch(string normalizedCandidate, string normalizedTarget)
    {
        if (normalizedTarget.Contains("speed", StringComparison.Ordinal))
        {
            if (!normalizedCandidate.Contains("speed", StringComparison.Ordinal))
            {
                return true;
            }

            if (normalizedCandidate.Contains("quality", StringComparison.Ordinal)
                || normalizedCandidate.Contains("audio", StringComparison.Ordinal)
                || normalizedCandidate.Contains("captions", StringComparison.Ordinal)
                || normalizedCandidate.Contains("subtitles", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (normalizedTarget.Contains("1 5x", StringComparison.Ordinal)
            || normalizedTarget.Contains("1 5 x", StringComparison.Ordinal)
            || normalizedTarget.Contains("1.5x", StringComparison.Ordinal)
            || normalizedTarget.Contains("1.50x", StringComparison.Ordinal))
        {
            if (!ContainsExactSpeedLabel(normalizedCandidate, "1.5x"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExactSpeedLabel(string candidateText, string targetLabel)
    {
        var normalizedTarget = NormalizeForMatching(targetLabel)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        var normalizedCandidate = NormalizeForMatching(candidateText)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalizedCandidate.Contains(normalizedTarget, StringComparison.Ordinal);
    }

    private static string BuildVideoSignature(OcrLayoutResult layout)
    {
        var signatureSource = GetCentralOcrText(layout);
        if (string.IsNullOrWhiteSpace(signatureSource))
        {
            signatureSource = layout.Text;
        }

        signatureSource = VideoProgressRangeRegex().Replace(signatureSource, " ");
        signatureSource = VideoTimecodeRegex().Replace(signatureSource, " ");
        return BuildLoopSignature(signatureSource);
    }

    private static bool ContainsSkipSignal(string text)
        => text.Contains("skip", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsResultsSummarySignal(string text)
        => text.Contains("goal accomplished", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("time spent", StringComparison.OrdinalIgnoreCase)
                && text.Contains("reward", StringComparison.OrdinalIgnoreCase)
                && text.Contains("accuracy", StringComparison.OrdinalIgnoreCase));

    private int GetNextVideoStandbyDelayMilliseconds(TimeSpan? remaining)
    {
        if (remaining.HasValue)
        {
            var knownDelay = (int)Math.Ceiling(remaining.Value.TotalMilliseconds) + KnownVideoCompletionBufferMilliseconds;
            return Math.Clamp(
                knownDelay,
                MinKnownVideoStandbyPollMilliseconds,
                MaxKnownVideoStandbyPollMilliseconds);
        }

        // Bias toward longer waits while still leaving a meaningful chance of shorter checks.
        var weighted = Math.Max(random.NextDouble(), random.NextDouble());
        return MinVideoStandbyPollMilliseconds
            + (int)Math.Round((MaxVideoStandbyPollMilliseconds - MinVideoStandbyPollMilliseconds) * weighted);
    }

    private async Task WaitWithVideoCountdownAsync(TimeSpan? remaining, int delayMilliseconds, CancellationToken cancellationToken)
    {
        var remainingDelay = Math.Max(0, delayMilliseconds);
        if (remainingDelay == 0)
        {
            ViewModel.SetVideoStandby(remaining);
            UpdateVisualState();
            return;
        }

        while (remainingDelay > 0)
        {
            ViewModel.SetVideoStandby(remaining);
            UpdateVisualState();

            var step = Math.Min(1000, remainingDelay);
            await Task.Delay(step, cancellationToken);
            remainingDelay -= step;

            if (remaining.HasValue)
            {
                remaining = remaining.Value - TimeSpan.FromMilliseconds(step);
                if (remaining.Value < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }
            }
        }
    }

    private void ResetVideoPlaybackTracking()
    {
        activeVideoSignature = null;
        activeVideoTotalDuration = null;
        hasHandledCurrentVideoSpeed = false;
    }

    private static bool TryExtractVideoProgress(OcrLayoutResult layout, out VideoProgress progress)
    {
        var source = GetCentralOcrText(layout);
        if (string.IsNullOrWhiteSpace(source))
        {
            source = layout.Text;
        }

        var match = VideoProgressRangeRegex().Match(source);
        if (!match.Success)
        {
            progress = default;
            return false;
        }

        if (!TryParseVideoTimecode(match.Groups["current"].Value, out var current)
            || !TryParseVideoTimecode(match.Groups["total"].Value, out var total))
        {
            progress = default;
            return false;
        }

        progress = new VideoProgress(current, total);
        return true;
    }

    private static TimeSpan? GetRemainingVideoDuration(OcrLayoutResult layout, bool adjustForPlaybackSpeed)
    {
        if (!TryExtractVideoProgress(layout, out var progress))
        {
            return null;
        }

        var remaining = progress.Total - progress.Current;
        if (remaining < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (!adjustForPlaybackSpeed)
        {
            return remaining;
        }

        var adjustedMilliseconds = remaining.TotalMilliseconds / VideoPlaybackSpeedMultiplier;
        return adjustedMilliseconds <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(adjustedMilliseconds);
    }

    private static bool TryParseVideoTimecode(string value, out TimeSpan time)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            time = default;
            return false;
        }

        if (parts.Length == 2
            && int.TryParse(parts[0], out var minutes)
            && int.TryParse(parts[1], out var seconds))
        {
            time = new TimeSpan(0, minutes, seconds);
            return true;
        }

        if (parts.Length == 3
            && int.TryParse(parts[0], out var hours)
            && int.TryParse(parts[1], out var mm)
            && int.TryParse(parts[2], out var ss))
        {
            time = new TimeSpan(hours, mm, ss);
            return true;
        }

        time = default;
        return false;
    }

    private static bool ContainsVideoSignal(OcrLayoutResult layout)
    {
        var text = layout.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var centerText = GetCentralOcrText(layout);
        if (ContainsVideoPhrase(centerText))
        {
            return true;
        }

        if (LooksLikeVideoTitleOnly(layout, centerText))
        {
            return true;
        }

        var hasTimecode = VideoTimecodeRegex().IsMatch(centerText);
        if (!hasTimecode)
        {
            return ContainsVideoPhrase(text) && text.Contains("video", StringComparison.OrdinalIgnoreCase);
        }

        return ContainsVideoControl(centerText) || ContainsVideoControl(text);
    }

    private static bool HasVisibleVideoPlaybackUi(OcrLayoutResult layout)
        => TryExtractVideoProgress(layout, out _)
            || ContainsVideoControl(GetCentralOcrText(layout))
            || ContainsVideoControl(layout.Text);

    private static bool TryGetMoreReasoning(string currentReasoning, out string strongerReasoning)
    {
        var normalizedReasoning = string.IsNullOrWhiteSpace(currentReasoning)
            ? "low"
            : currentReasoning.Trim().ToLowerInvariant();
        var index = Array.IndexOf(ReasoningEscalationOrder, normalizedReasoning);
        if (index < 0)
        {
            index = Array.IndexOf(ReasoningEscalationOrder, "low");
        }

        strongerReasoning = string.Empty;
        if (index < 0 || index >= ReasoningEscalationOrder.Length - 1)
        {
            return false;
        }

        strongerReasoning = ReasoningEscalationOrder[index + 1];
        return true;
    }

    private static string GetCentralOcrText(OcrLayoutResult layout)
    {
        var regions = layout.Lines.Count > 0 ? layout.Lines : layout.Words;
        if (regions.Count == 0)
        {
            return layout.Text;
        }

        var maxRight = regions.Max(region => region.Bounds.Right);
        var maxBottom = regions.Max(region => region.Bounds.Bottom);
        if (maxRight <= 0 || maxBottom <= 0)
        {
            return layout.Text;
        }

        var leftBound = maxRight * 0.22;
        var rightBound = maxRight * 0.78;
        var topBound = maxBottom * 0.22;
        var bottomBound = maxBottom * 0.78;

        var centerRegions = regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .Where(region =>
            {
                var centerX = region.Bounds.Left + (region.Bounds.Width / 2d);
                var centerY = region.Bounds.Top + (region.Bounds.Height / 2d);
                return centerX >= leftBound
                    && centerX <= rightBound
                    && centerY >= topBound
                    && centerY <= bottomBound;
            })
            .OrderBy(region => region.Bounds.Top)
            .ThenBy(region => region.Bounds.Left)
            .Select(region => region.Text.Trim())
            .ToArray();

        return centerRegions.Length == 0
            ? layout.Text
            : string.Join(Environment.NewLine, centerRegions);
    }

    private static bool ContainsVideoPhrase(string text)
        => text.Contains("watch video", StringComparison.OrdinalIgnoreCase)
            || text.Contains("watch the video", StringComparison.OrdinalIgnoreCase)
            || text.Contains("play video", StringComparison.OrdinalIgnoreCase)
            || text.Contains("video will", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tap to unmute", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tap to play", StringComparison.OrdinalIgnoreCase)
            || text.Contains("replay video", StringComparison.OrdinalIgnoreCase)
            || text.Contains("skip ad", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsVideoControl(string text)
        => text.Contains("pause", StringComparison.OrdinalIgnoreCase)
            || text.Contains("play", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mute", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unmute", StringComparison.OrdinalIgnoreCase)
            || text.Contains("replay", StringComparison.OrdinalIgnoreCase)
            || text.Contains("reverse", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rewind", StringComparison.OrdinalIgnoreCase)
            || text.Contains("back 10", StringComparison.OrdinalIgnoreCase)
            || text.Contains("back 15", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gear", StringComparison.OrdinalIgnoreCase)
            || text.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fullscreen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("live", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeVideoTitleOnly(OcrLayoutResult layout, string centerText)
    {
        if (string.IsNullOrWhiteSpace(centerText))
        {
            return false;
        }

        var lineCount = layout.Lines.Count(region => !string.IsNullOrWhiteSpace(region.Text));
        var wordCount = layout.Words.Count(region => !string.IsNullOrWhiteSpace(region.Text));
        if (lineCount > 2 || wordCount > 10)
        {
            return false;
        }

        if (ContainsQuestionSignal(layout.Text) || ContainsQuestionSignal(centerText))
        {
            return false;
        }

        var normalizedCenter = centerText.Trim();
        return normalizedCenter.Length >= 6
            && normalizedCenter.Length <= 120
            && !normalizedCenter.Contains(':')
            && !normalizedCenter.Contains('?')
            && !ContainsAnswerCue(normalizedCenter);
    }

    private static bool ContainsQuestionSignal(string text)
        => text.Contains('?')
            || text.Contains("select", StringComparison.OrdinalIgnoreCase)
            || text.Contains("choose", StringComparison.OrdinalIgnoreCase)
            || text.Contains("which", StringComparison.OrdinalIgnoreCase)
            || text.Contains("what", StringComparison.OrdinalIgnoreCase)
            || text.Contains("how many", StringComparison.OrdinalIgnoreCase)
            || text.Contains("true or false", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAnswerCue(string text)
        => AnswerCueRegex().IsMatch(text);

    private static bool IsLikelyQuestionScreen(OcrLayoutResult layout)
    {
        var text = layout.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var centerText = GetCentralOcrText(layout);
        var hasQuestionLanguage = ContainsQuestionSignal(centerText) || ContainsQuestionSignal(text);
        var answerCueCount = CountAnswerCues(layout);
        if (answerCueCount >= 2)
        {
            return true;
        }

        return hasQuestionLanguage && answerCueCount >= 1;
    }

    private static int CountAnswerCues(OcrLayoutResult layout)
        => layout.Lines.Count(region => !string.IsNullOrWhiteSpace(region.Text) && ContainsAnswerCue(region.Text));

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b")]
    private static partial Regex VideoTimecodeRegex();

    [GeneratedRegex(@"(?<current>\b\d{1,2}:\d{2}(?::\d{2})?\b)\s*/\s*(?<total>\b\d{1,2}:\d{2}(?::\d{2})?\b)")]
    private static partial Regex VideoProgressRangeRegex();

    [GeneratedRegex(@"(^|\s)([A-Da-d][\.\)]|[1-4][\.\)])\s")]
    private static partial Regex AnswerCueRegex();

    private static AnswerResult CreateLoopFinishedResult(int answeredQuestions)
        => new()
        {
            Status = AnswerStatus.Success,
            Text = $"Finished. Answered {answeredQuestions} question{(answeredQuestions == 1 ? string.Empty : "s")}."
        };

    private static void TryDeleteCapture(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        try
        {
            File.Delete(imagePath);
        }
        catch
        {
            // ignore best-effort cleanup
        }
    }

    private static void ClickAbsolutePoint(int x, int y)
    {
        var inputs = new[]
        {
            CreateMouseInput(x, y, NativeMethods.MouseeventfMove | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualdesk),
            CreateMouseInput(x, y, NativeMethods.MouseeventfLeftdown | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualdesk),
            CreateMouseInput(x, y, NativeMethods.MouseeventfLeftup | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualdesk)
        };

        var inputSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>();
        NativeMethods.SendInput((uint)inputs.Length, inputs, inputSize);
    }

    private static void ClickRegion(Rectangle captureBounds, Rectangle regionBounds)
    {
        var x = captureBounds.Left + regionBounds.Left + (regionBounds.Width / 2);
        var y = captureBounds.Top + regionBounds.Top + (regionBounds.Height / 2);
        ClickAbsolutePoint(x, y);
    }

    private static NativeMethods.Input CreateMouseInput(int x, int y, uint flags)
        => new()
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    X = NormalizeCoordinate(x, NativeMethods.SmXvirtualscreen, NativeMethods.SmCxvirtualscreen),
                    Y = NormalizeCoordinate(y, NativeMethods.SmYvirtualscreen, NativeMethods.SmCyvirtualscreen),
                    MouseData = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

    private static int NormalizeCoordinate(int value, int originMetric, int sizeMetric)
    {
        var origin = NativeMethods.GetSystemMetrics(originMetric);
        var size = Math.Max(1, NativeMethods.GetSystemMetrics(sizeMetric) - 1);
        return (int)Math.Round((value - origin) * 65535d / size);
    }

    private Windows.Graphics.PointInt32 GetVisibleWidgetPosition(int width, int height)
    {
        var virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SmXvirtualscreen);
        var virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SmYvirtualscreen);
        var virtualWidth = Math.Max(width, NativeMethods.GetSystemMetrics(NativeMethods.SmCxvirtualscreen));
        var virtualHeight = Math.Max(height, NativeMethods.GetSystemMetrics(NativeMethods.SmCyvirtualscreen));
        var virtualRight = virtualLeft + virtualWidth;
        var virtualBottom = virtualTop + virtualHeight;

        var minX = virtualLeft;
        var minY = virtualTop;
        var maxX = Math.Max(minX, virtualRight - width);
        var maxY = Math.Max(minY, virtualBottom - height);

        var x = (int)Math.Round(appState.WidgetBounds.X);
        var y = (int)Math.Round(appState.WidgetBounds.Y);

        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        appState.UpdateWidgetBounds(x, y, width, height);
        return new Windows.Graphics.PointInt32(x, y);
    }

    private static Point GetVideoSettingsGearPoint(Rectangle bounds)
        => new(
            bounds.Left + (int)(bounds.Width * VideoSettingsGearRelativeX),
            bounds.Top + (int)(bounds.Height * VideoSettingsGearRelativeY));

    private static IEnumerable<Point> GetVideoSettingsGearClickCandidates(Rectangle bounds)
    {
        var basePoint = GetVideoSettingsGearPoint(bounds);
        yield return basePoint;
        yield return new Point(basePoint.X - 16, basePoint.Y);
        yield return new Point(basePoint.X + 16, basePoint.Y);
        yield return new Point(basePoint.X, basePoint.Y - 12);
        yield return new Point(basePoint.X, basePoint.Y + 12);
    }

    private static Point GetVideoSpeedMenuPoint(Rectangle bounds)
        => new(
            bounds.Left + (int)(bounds.Width * VideoSpeedMenuRelativeX),
            bounds.Top + (int)(bounds.Height * VideoSpeedMenuRelativeY));

    private static Point GetVideoOnePointFiveSpeedPoint(Rectangle bounds)
        => new(
            bounds.Left + (int)(bounds.Width * VideoSpeedOnePointFiveRelativeX),
            bounds.Top + (int)(bounds.Height * VideoSpeedOnePointFiveRelativeY));

    private async Task<bool> IsVideoSettingsMenuOpenAsync(CancellationToken cancellationToken = default)
    {
        CaptureLayoutResult? snapshot = null;

        try
        {
            snapshot = await CaptureCurrentLayoutUnderCursorAsync(showCaptureStatus: false, showOcrStatus: false, cancellationToken: cancellationToken);
            return TryFindMatchingRegion(
                snapshot.Layout,
                ["speed", "playback speed", "quality", "audio", "captions", "subtitles"],
                out _);
        }
        finally
        {
            if (snapshot is not null)
            {
                TryDeleteCapture(snapshot.Capture.ImagePath);
            }
        }
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

        // Capture on the root surface so PointerMoved fires even outside the drag column
        WidgetSurface.CapturePointer(e.Pointer);
        DragHandleArea.ShowDraggingCursor();

        NativeMethods.GetCursorPos(out dragStartCursorPosition);
        dragStartWindowPosition = appWindow?.Position
            ?? new Windows.Graphics.PointInt32((int)appState.WidgetBounds.X, (int)appState.WidgetBounds.Y);
        isDragging = true;
        e.Handled = true;
    }

    private void OnDragHandlePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging)
        {
            DragHandleArea.ShowHoverCursor();
        }
    }

    private void OnDragHandlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging)
        {
            DragHandleArea.ClearCursor();
        }
    }

    private void OnDragAreaPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging) return;

        DragHandleArea.ShowDraggingCursor();

        NativeMethods.GetCursorPos(out var currentCursor);
        var dx = currentCursor.X - dragStartCursorPosition.X;
        var dy = currentCursor.Y - dragStartCursorPosition.Y;

        appWindow?.Move(new Windows.Graphics.PointInt32(
            dragStartWindowPosition.X + dx,
            dragStartWindowPosition.Y + dy));

        e.Handled = true;
    }

    private void OnDragAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging) return;

        WidgetSurface.ReleasePointerCaptures();
        isDragging = false;

        if (IsPointerWithinElement(e, DragHandleArea))
        {
            DragHandleArea.ShowHoverCursor();
        }
        else
        {
            DragHandleArea.ClearCursor();
        }

        e.Handled = true;
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

    private void OnOpenMainWindowMenuItemClicked(object sender, RoutedEventArgs e)
    {
        App.CurrentApp.ShowMainWindow();
    }

    private void OnForceStopMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            return;
        }

        CancelActiveAnswer();
    }

    private void OnHideWidgetMenuItemClicked(object sender, RoutedEventArgs e)
    {
        this.HideWindow();
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
        var isThinking = ViewModel.ShowStatus;
        var showMessage = ViewModel.ShowMessage && !isThinking;
        var showActionButton = ViewModel.ShowActionButton;

        AnswerButton.Visibility = showActionButton ? Visibility.Visible : Visibility.Collapsed;
        ThinkingTextPresenter.Visibility = isThinking ? Visibility.Visible : Visibility.Collapsed;
        MessageTextBlock.Visibility = showMessage ? Visibility.Visible : Visibility.Collapsed;
        MessageTextBlock.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            ViewModel.IsError ? "WidgetErrorTextBrush" : "WidgetTextBrush"];
        StatusProgressRing.Visibility = ViewModel.ShowStatusSpinner ? Visibility.Visible : Visibility.Collapsed;
        OcrStatusIcon.Visibility = ViewModel.ShowOcrStatusIcon ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotStatusIcon.Visibility = ViewModel.ShowScreenshotStatusIcon ? Visibility.Visible : Visibility.Collapsed;
        ThinkingTextBlock.Foreground = ViewModel.UseWarningStatusStyle
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WidgetWarningTextBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)RootGrid.Resources["WidgetThinkingTextShimmerBrush"];

        UpdateThinkingAnimation(isThinking);
        UpdateThinkingReveal(isThinking);
        UpdateMessageReveal(showMessage);
        UpdateActionButtonReveal(showActionButton);
    }

    private async Task<ScreenCaptureResult> CaptureDisplayUnderCursorWithStatusAsync(bool showStatus = true, CancellationToken cancellationToken = default)
    {
        this.HideWindow();
        await Task.Delay(120, cancellationToken);
        var capture = await screenCaptureService.CaptureDisplayUnderCursorAsync(cancellationToken);
        this.ShowWin32Window(false);
        BringToFront();
        if (showStatus)
        {
            ViewModel.SetScreenshotTaken();
            UpdateVisualState();
            await Task.Delay(180, cancellationToken);
        }

        return capture;
    }

    private async Task<ScreenCaptureResult> CaptureAtPointWithStatusAsync(int x, int y, bool clickCenter, bool showStatus = true, CancellationToken cancellationToken = default)
    {
        this.HideWindow();
        await Task.Delay(120, cancellationToken);
        var capture = await CaptureAtPointAsync(x, y, clickCenter, cancellationToken: cancellationToken);
        this.ShowWin32Window(false);
        BringToFront();
        if (showStatus)
        {
            ViewModel.SetScreenshotTaken();
            UpdateVisualState();
            await Task.Delay(180, cancellationToken);
        }

        return capture;
    }

    private async Task<OcrLayoutResult> ExtractLayoutWithStatusAsync(string imagePath, bool showStatus = true, CancellationToken cancellationToken = default)
    {
        if (showStatus)
        {
            ViewModel.SetExtractingText();
            UpdateVisualState();
            await Task.Delay(120, cancellationToken);
        }

        return await ocrService.ExtractLayoutAsync(imagePath, cancellationToken);
    }

    private void UpdateThinkingAnimation(bool isThinking)
    {
        if (isThinking)
        {
            ActiveHighlightBorder.Visibility = Visibility.Visible;

            if (!wasShowingThinkingText)
            {
                SubtleBorderGlowStoryboard.Stop();
                ThinkingTextShimmerStoryboard.Stop();
                ThinkingTextShimmerTransform.X = ThinkingTextShimmerStartX;
                SubtleBorderGlowStoryboard.Begin();
                ThinkingTextShimmerStoryboard.Begin();
            }
        }
        else
        {
            SubtleBorderGlowStoryboard.Stop();
            ThinkingTextShimmerStoryboard.Stop();
            ThinkingTextShimmerTransform.X = ThinkingTextShimmerStartX;
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

    private static bool IsPointerWithinElement(PointerRoutedEventArgs e, FrameworkElement element)
    {
        var point = e.GetCurrentPoint(element).Position;
        return point.X >= 0
            && point.Y >= 0
            && point.X <= element.ActualWidth
            && point.Y <= element.ActualHeight;
    }

    private sealed record AnswerIterationResult(AnswerResult Result, AgentClickResult ClickResult, OcrLayoutResult OcrLayout);
    private sealed record VideoProbeResult(OcrLayoutResult OcrLayout, bool VideoDetected);
    private sealed record CaptureLayoutResult(ScreenCaptureResult Capture, OcrLayoutResult Layout);
    private readonly record struct VideoProgress(TimeSpan Current, TimeSpan Total);
}
