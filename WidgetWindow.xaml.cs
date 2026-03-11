using Indolent.Helpers;
using Indolent.Services;
using System.Drawing;
using System.Text.RegularExpressions;

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
    private const int LoopTransitionPollMilliseconds = 900;
    private const int MaxLoopTransitionPolls = 7;
    private const int MaxAgentLoopQuestions = 50;
    private const double VideoSettingsGearRelativeX = 0.723;
    private const double VideoSettingsGearRelativeY = 0.808;
    private const double VideoSpeedMenuRelativeX = 0.694;
    private const double VideoSpeedMenuRelativeY = 0.734;
    private const double VideoSpeedOnePointFiveRelativeX = 0.694;
    private const double VideoSpeedOnePointFiveRelativeY = 0.764;
    private const string OcrAnswerPrompt = "Answer the user's question from this OCR text. OCR may contain noise. If it is multiple choice, return only the best option unless a short clarification is necessary. Keep it brief.";
    private const string ScreenshotAnswerPrompt = "Answer the visible question from this screenshot. Use the image as the source of truth, and use any OCR text only as a hint. If it is multiple choice, return only the best option unless a short clarification is necessary. Keep it brief.";
    private const string AgentOcrAnswerPrompt = "Answer the user's question from this OCR text. OCR may contain noise. Return only the exact answer text or option label as shown on screen. No explanation.";
    private const string AgentScreenshotAnswerPrompt = "Answer the visible question from this screenshot. Use the image as the source of truth and OCR only as a hint. Return only the exact answer text or option label as shown on screen. No explanation.";
    private const string VideoDetectionPrompt = "Check whether this screenshot is a video/player state rather than a question screen. Return only VIDEO or NOT_VIDEO. Return VIDEO if you can see player controls such as play/pause, settings/gear, rewind/reverse, progress UI, or a title/interstitial without a visible question.";
    private readonly AppState appState;
    private readonly IAgentClickService agentClickService;
    private readonly ICodexCliService codexCliService;
    private readonly IOcrService ocrService;
    private readonly IScreenCaptureService screenCaptureService;
    private readonly ISettingsStore settingsStore;
    private readonly Random random = new();

    private AppWindow? appWindow;
    private bool wasShowingThinkingText;
    private bool wasShowingMessageText;
    private bool wasShowingActionButton;
    private string? activeVideoSignature;
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

        try
        {
            var result = appState.AgentModeEnabled && appState.AgentLoopEnabled
                ? await RunAgentLoopAsync()
                : await RunSingleAnswerAsync();

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

            await appState.PersistAsync(settingsStore);
        }
    }

    private async Task<AnswerResult> RunSingleAnswerAsync()
    {
        var iteration = await ExecuteAnswerIterationAsync();
        return iteration.Result;
    }

    private async Task<AnswerResult> RunAgentLoopAsync()
    {
        var answeredQuestions = 0;

        while (answeredQuestions < MaxAgentLoopQuestions)
        {
            await WaitForVideoToFinishAsync();

            var currentReasoning = appState.SelectedReasoningEffort;
            AnswerIterationResult? iteration = null;
            OcrLayoutResult? followUpOcr = null;

            while (true)
            {
                iteration = await ExecuteAnswerIterationAsync(currentReasoning);
                if (!iteration.Result.IsSuccess || !iteration.ClickResult.Clicked)
                {
                    return CreateLoopFinishedResult(answeredQuestions);
                }

                var questionSignature = BuildLoopSignature(iteration.OcrLayout.Text);
                followUpOcr = await WaitForPostClickSnapshotAsync(questionSignature);

                if (!ContainsSkipSignal(followUpOcr.Text))
                {
                    break;
                }

                if (!TryGetMoreReasoning(currentReasoning, out var strongerReasoning))
                {
                    return CreateLoopFinishedResult(answeredQuestions);
                }

                currentReasoning = strongerReasoning;
            }

            if (iteration is null || followUpOcr is null)
            {
                return CreateLoopFinishedResult(answeredQuestions);
            }

            if (ContainsResultsSummarySignal(followUpOcr.Text))
            {
                if (!await TryAdvanceResultsSummaryAsync(followUpOcr))
                {
                    return CreateLoopFinishedResult(answeredQuestions);
                }

                followUpOcr = await WaitForPostClickSnapshotAsync(BuildLoopSignature(followUpOcr.Text));
            }

            var currentSignature = BuildLoopSignature(followUpOcr.Text);
            if (string.IsNullOrWhiteSpace(currentSignature)
                || string.Equals(currentSignature, BuildLoopSignature(iteration.OcrLayout.Text), StringComparison.Ordinal))
            {
                return CreateLoopFinishedResult(answeredQuestions);
            }

            answeredQuestions++;
        }

        return CreateLoopFinishedResult(answeredQuestions);
    }

    private async Task<AnswerIterationResult> ExecuteAnswerIterationAsync(string? reasoningEffortOverride = null)
    {
        this.HideWindow();
        await Task.Delay(120);
        var capture = await screenCaptureService.CaptureDisplayUnderCursorAsync();

        try
        {
            this.ShowWin32Window(false);
            BringToFront();
            await Task.Delay(50);

            var ocrLayout = await ocrService.ExtractLayoutAsync(capture.ImagePath);
            var reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffortOverride)
                ? appState.SelectedReasoningEffort
                : reasoningEffortOverride;
            var result = await GetBestAnswerAsync(capture.ImagePath, ocrLayout.Text, appState.AgentModeEnabled, reasoningEffort);
            var clickResult = new AgentClickResult();

            if (appState.AgentModeEnabled && result.IsSuccess)
            {
                this.HideWindow();
                clickResult = await agentClickService.TryClickAnswerAsync(
                    result.Text,
                    capture,
                    ocrLayout,
                    appState.SelectedModel,
                    reasoningEffort);
                this.ShowWin32Window(false);
                BringToFront();
            }

            return new AnswerIterationResult(result, clickResult, ocrLayout);
        }
        finally
        {
            TryDeleteCapture(capture.ImagePath);
        }
    }

    private async Task<OcrLayoutResult> CaptureOcrLayoutSnapshotAsync()
    {
        this.HideWindow();
        await Task.Delay(120);
        var capture = await screenCaptureService.CaptureDisplayUnderCursorAsync();

        try
        {
            return await ocrService.ExtractLayoutAsync(capture.ImagePath);
        }
        finally
        {
            this.ShowWin32Window(false);
            BringToFront();
            TryDeleteCapture(capture.ImagePath);
        }
    }

    private async Task<OcrLayoutResult> WaitForVideoToFinishAsync(OcrLayoutResult? initialSnapshot = null)
    {
        var snapshot = initialSnapshot ?? await CaptureOcrLayoutSnapshotAsync();
        if (ContainsResultsSummarySignal(snapshot.Text))
        {
            return snapshot;
        }

        var videoDetected = false;
        if (!ContainsQuestionSignal(snapshot.Text))
        {
            var probe = await CaptureCenteredHoverSnapshotAsync();
            snapshot = probe.OcrLayout;
            videoDetected = probe.VideoDetected;
        }

        while (!ContainsResultsSummarySignal(snapshot.Text) && (videoDetected || ContainsVideoSignal(snapshot)))
        {
            ViewModel.SetVideoStandby();
            UpdateVisualState();
            await Task.Delay(GetNextVideoStandbyDelayMilliseconds());
            var probe = await CaptureCenteredHoverSnapshotAsync();
            snapshot = probe.OcrLayout;
            videoDetected = probe.VideoDetected;
        }

        ViewModel.SetThinking();
        UpdateVisualState();
        ResetVideoPlaybackTracking();
        return snapshot;
    }

    private async Task<OcrLayoutResult> WaitForPostClickSnapshotAsync(string questionSignature)
    {
        OcrLayoutResult? lastSnapshot = null;

        for (var attempt = 0; attempt < MaxLoopTransitionPolls; attempt++)
        {
            await Task.Delay(attempt == 0 ? AgentLoopDelayMilliseconds : LoopTransitionPollMilliseconds);
            lastSnapshot = await CaptureOcrLayoutSnapshotAsync();
            lastSnapshot = await WaitForVideoToFinishAsync(lastSnapshot);

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

    private async Task<VideoProbeResult> CaptureCenteredHoverSnapshotAsync()
    {
        NativeMethods.GetCursorPos(out var currentCursor);
        var monitor = NativeMethods.MonitorFromPoint(currentCursor, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new VideoProbeResult(await CaptureOcrLayoutSnapshotAsync(), false);
        }

        var centerX = monitorInfo.Monitor.Left + ((monitorInfo.Monitor.Right - monitorInfo.Monitor.Left) / 2);
        var centerY = monitorInfo.Monitor.Top + ((monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top) / 2);
        this.HideWindow();
        await Task.Delay(120);

        var probeCapture = await CaptureAtPointAsync(centerX, centerY, clickCenter: true);
        try
        {
            this.ShowWin32Window(false);
            BringToFront();
            await Task.Delay(50);

            var snapshot = await ocrService.ExtractLayoutAsync(probeCapture.ImagePath);
            var videoDetected = ContainsVideoSignal(snapshot) || await DetectVideoStateFromScreenshotAsync(probeCapture.ImagePath, snapshot.Text);
            if (videoDetected)
            {
                await EnsureCurrentVideoPlaybackConfiguredAsync(probeCapture, snapshot);
                this.HideWindow();
                ClickAbsolutePoint(centerX, centerY);
                await Task.Delay(220);
                this.ShowWin32Window(false);
                BringToFront();
            }

            return new VideoProbeResult(snapshot, videoDetected);
        }
        finally
        {
            TryDeleteCapture(probeCapture.ImagePath);
        }
    }

    private async Task<ScreenCaptureResult> CaptureAtPointAsync(int x, int y, bool clickCenter)
    {
        NativeMethods.SetCursorPos(x, y);
        await Task.Delay(180);
        if (clickCenter)
        {
            ClickAbsolutePoint(x, y);
            await Task.Delay(320);
        }

        return await screenCaptureService.CaptureDisplayUnderCursorAsync();
    }

    private async Task<bool> TryAdvanceResultsSummaryAsync(OcrLayoutResult currentLayout)
    {
        if (!ContainsResultsSummarySignal(currentLayout.Text))
        {
            return false;
        }

        this.HideWindow();
        await Task.Delay(120);
        var capture = await screenCaptureService.CaptureDisplayUnderCursorAsync();

        try
        {
            this.ShowWin32Window(false);
            BringToFront();
            await Task.Delay(50);

            var ocrLayout = await ocrService.ExtractLayoutAsync(capture.ImagePath);
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
                appState.SelectedReasoningEffort);
            this.ShowWin32Window(false);
            BringToFront();
            return clickResult.Clicked;
        }
        finally
        {
            TryDeleteCapture(capture.ImagePath);
        }
    }

    private async Task<AnswerResult> GetBestAnswerAsync(string capturePath, string screenText, bool agentModeEnabled, string reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(screenText))
        {
            return await AnswerFromScreenshotAsync(capturePath, screenText, agentModeEnabled, reasoningEffort);
        }

        var ocrResult = await codexCliService.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            Prompt = agentModeEnabled ? AgentOcrAnswerPrompt : OcrAnswerPrompt,
            ReasoningEffort = reasoningEffort,
            RequestedAt = DateTimeOffset.Now
        });

        return NeedsScreenshotFallback(ocrResult)
            ? await AnswerFromScreenshotAsync(capturePath, screenText, agentModeEnabled, reasoningEffort)
            : ocrResult;
    }

    private async Task<bool> DetectVideoStateFromScreenshotAsync(string capturePath, string screenText)
    {
        var result = await codexCliService.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            ScreenshotPath = capturePath,
            Prompt = VideoDetectionPrompt,
            ReasoningEffort = "low",
            RequestedAt = DateTimeOffset.Now
        });

        return result.IsSuccess
            && result.Text.Contains("VIDEO", StringComparison.OrdinalIgnoreCase)
            && !result.Text.Contains("NOT_VIDEO", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureCurrentVideoPlaybackConfiguredAsync(ScreenCaptureResult capture, OcrLayoutResult layout)
    {
        var videoSignature = BuildVideoSignature(layout);
        if (!string.Equals(activeVideoSignature, videoSignature, StringComparison.Ordinal))
        {
            activeVideoSignature = videoSignature;
            hasHandledCurrentVideoSpeed = false;
        }

        if (hasHandledCurrentVideoSpeed)
        {
            return;
        }

        hasHandledCurrentVideoSpeed = true;
        await TryConfigureVideoPlaybackSpeedAsync(capture, layout);
    }

    private async Task TryConfigureVideoPlaybackSpeedAsync(ScreenCaptureResult capture, OcrLayoutResult layout)
    {
        if (!await TryClickVideoSettingsGearAsync(capture.Bounds))
        {
            return;
        }

        await Task.Delay(260);
        await TryClickVideoSpeedMenuAsync(capture.Bounds);
        await Task.Delay(260);
        await TryClickVideoOnePointFiveSpeedAsync(capture.Bounds);
    }

    private async Task<bool> TryClickVideoSettingsGearAsync(Rectangle captureBounds)
    {
        var point = GetVideoSettingsGearPoint(captureBounds);
        this.HideWindow();
        ClickAbsolutePoint(point.X, point.Y);
        await Task.Delay(220);
        this.ShowWin32Window(false);
        BringToFront();
        return true;
    }

    private async Task<bool> TryClickVideoSpeedMenuAsync(Rectangle captureBounds)
    {
        var point = GetVideoSpeedMenuPoint(captureBounds);
        this.HideWindow();
        ClickAbsolutePoint(point.X, point.Y);
        await Task.Delay(220);
        this.ShowWin32Window(false);
        BringToFront();
        return true;
    }

    private async Task<bool> TryClickVideoOnePointFiveSpeedAsync(Rectangle captureBounds)
    {
        var point = GetVideoOnePointFiveSpeedPoint(captureBounds);
        this.HideWindow();
        ClickAbsolutePoint(point.X, point.Y);
        await Task.Delay(220);
        this.ShowWin32Window(false);
        BringToFront();
        return true;
    }

    private async Task<CaptureLayoutResult> CaptureCurrentLayoutUnderCursorAsync()
    {
        this.HideWindow();
        await Task.Delay(120);
        var capture = await screenCaptureService.CaptureDisplayUnderCursorAsync();
        this.ShowWin32Window(false);
        BringToFront();
        await Task.Delay(50);
        var layout = await ocrService.ExtractLayoutAsync(capture.ImagePath);
        return new CaptureLayoutResult(capture, layout);
    }

    private Task<AnswerResult> AnswerFromScreenshotAsync(string capturePath, string screenText, bool agentModeEnabled, string reasoningEffort)
        => codexCliService.AnswerAsync(new AnswerRequest
        {
            Model = appState.SelectedModel,
            ScreenText = screenText,
            ScreenshotPath = capturePath,
            Prompt = agentModeEnabled ? AgentScreenshotAnswerPrompt : ScreenshotAnswerPrompt,
            ReasoningEffort = reasoningEffort,
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

    private static string BuildVideoSignature(OcrLayoutResult layout)
    {
        var signatureSource = GetCentralOcrText(layout);
        if (string.IsNullOrWhiteSpace(signatureSource))
        {
            signatureSource = layout.Text;
        }

        return BuildLoopSignature(signatureSource);
    }

    private static bool ContainsSkipSignal(string text)
        => text.Contains("skip", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsResultsSummarySignal(string text)
        => text.Contains("goal accomplished", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("time spent", StringComparison.OrdinalIgnoreCase)
                && text.Contains("reward", StringComparison.OrdinalIgnoreCase)
                && text.Contains("accuracy", StringComparison.OrdinalIgnoreCase));

    private int GetNextVideoStandbyDelayMilliseconds()
    {
        // Bias toward longer waits while still leaving a meaningful chance of shorter checks.
        var weighted = Math.Max(random.NextDouble(), random.NextDouble());
        return MinVideoStandbyPollMilliseconds
            + (int)Math.Round((MaxVideoStandbyPollMilliseconds - MinVideoStandbyPollMilliseconds) * weighted);
    }

    private void ResetVideoPlaybackTracking()
    {
        activeVideoSignature = null;
        hasHandledCurrentVideoSpeed = false;
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

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b")]
    private static partial Regex VideoTimecodeRegex();

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

    private static Point GetVideoSettingsGearPoint(Rectangle bounds)
        => new(
            bounds.Left + (int)(bounds.Width * VideoSettingsGearRelativeX),
            bounds.Top + (int)(bounds.Height * VideoSettingsGearRelativeY));

    private static Point GetVideoSpeedMenuPoint(Rectangle bounds)
        => new(
            bounds.Left + (int)(bounds.Width * VideoSpeedMenuRelativeX),
            bounds.Top + (int)(bounds.Height * VideoSpeedMenuRelativeY));

    private static Point GetVideoOnePointFiveSpeedPoint(Rectangle bounds)
        => new(
            bounds.Left + (int)(bounds.Width * VideoSpeedOnePointFiveRelativeX),
            bounds.Top + (int)(bounds.Height * VideoSpeedOnePointFiveRelativeY));


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
}
