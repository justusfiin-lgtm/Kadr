using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kadr;

/// <summary>
/// Обычное окно Windows (HWND) внутри WPF-окна. В него рисует mpv:
/// WPF сам видео не показывает, он только выделяет место под картинку.
/// </summary>
internal sealed class VideoHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int SS_BLACKRECT = 0x00000004; // чёрный фон, пока mpv ещё ничего не нарисовал

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwnd = CreateWindowEx(0, "static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_BLACKRECT,
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception();
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
