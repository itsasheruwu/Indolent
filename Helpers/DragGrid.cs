using System.IO;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Indolent.Helpers;

internal sealed class DragGrid : Grid
{
    private static readonly IntPtr HoverCursorHandle = LoadCursorHandle("grab.cur");
    private static readonly IntPtr DraggingCursorHandle = LoadCursorHandle("grabbing.cur");

    public void ShowHoverCursor()
    {
        if (HoverCursorHandle != IntPtr.Zero)
        {
            ProtectedCursor = null;
            NativeMethods.SetCursor(HoverCursorHandle);
            return;
        }

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    public void ShowDraggingCursor()
    {
        if (DraggingCursorHandle != IntPtr.Zero)
        {
            ProtectedCursor = null;
            NativeMethods.SetCursor(DraggingCursorHandle);
            return;
        }

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    public void ClearCursor()
    {
        ProtectedCursor = null;
        NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IdcArrow));
    }

    private static IntPtr LoadCursorHandle(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path))
        {
            return IntPtr.Zero;
        }

        return NativeMethods.LoadImage(
            IntPtr.Zero,
            path,
            NativeMethods.ImageCursor,
            0,
            0,
            NativeMethods.LrLoadFromFile);
    }
}

internal sealed class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape shape)
    {
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    public void ClearCursor()
    {
        ProtectedCursor = null;
    }
}
