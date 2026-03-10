using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Indolent.Helpers;

internal static class WindowExtensions
{
    internal static IntPtr GetWindowHandle(this Window window)
        => WindowNative.GetWindowHandle(window);

    internal static AppWindow GetAppWindow(this Window window)
        => AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(window.GetWindowHandle()));

    internal static void HideWindow(this Window window)
        => NativeMethods.ShowWindow(window.GetWindowHandle(), NativeMethods.SwHide);

    internal static void ShowWin32Window(this Window window, bool activate)
        => NativeMethods.ShowWindow(window.GetWindowHandle(), activate ? NativeMethods.SwShow : NativeMethods.SwShowNoActivate);
}
