using Indolent.Helpers;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Indolent.Services;

public sealed class TrayService : ITrayService
{
    private TaskbarIcon? notifyIcon;

    public void Initialize(DispatcherQueue dispatcherQueue, Action showSettings, Action exitApplication)
    {
        if (notifyIcon is not null)
        {
            return;
        }

        var openItem = new MenuFlyoutItem { Text = "Open Settings" };
        openItem.Click += (_, _) => dispatcherQueue.TryEnqueue(() => showSettings());

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => dispatcherQueue.TryEnqueue(() => exitApplication());

        var menu = new MenuFlyout();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);

        notifyIcon = new TaskbarIcon
        {
            ToolTipText = "Indolent",
            ContextFlyout = menu,
            LeftClickCommand = new DelegateCommand(() => dispatcherQueue.TryEnqueue(() => showSettings())),
            IconSource = new GeneratedIconSource
            {
                Text = "I",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Bahnschrift SemiBold"),
                FontSize = 72,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 245, 242, 255)),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 41, 38, 167)),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 198, 170, 255)),
                BorderThickness = 8,
                CornerRadius = new CornerRadius(24)
            }
        };

        notifyIcon.ForceCreate();
    }

    public void Dispose()
    {
        if (notifyIcon is null)
        {
            return;
        }

        notifyIcon.Dispose();
        notifyIcon = null;
    }
}
