using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using static Kadr.NativeMethods;

namespace Kadr;

/// <summary>
/// Обычное окно Windows (HWND) внутри WPF-окна. В него рисует mpv:
/// WPF сам видео не показывает, он только выделяет место под картинку.
/// </summary>
/// <remarks>
/// Мышь над картинкой должна доставаться WPF-окну, а не mpv. Для этого:
/// - наше окно — класса «static»: такие окна «прозрачны» для мыши, щелчки проходят к родителю;
/// - окно, которое mpv создаёт внутри нашего, отключаем (<see cref="PassMouseThrough"/>):
///   отключённые дочерние окна мышь тоже пропускают к родителю.
/// </remarks>
internal sealed class VideoHost : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // SS_BLACKRECT — чёрный фон, пока mpv ещё ничего не нарисовал.
        var hwnd = CreateWindowEx(0, "static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_BLACKRECT,
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception();
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    /// <summary>Вызывать каждый раз, когда mpv мог создать своё окно (после смены видео).</summary>
    public void PassMouseThrough() => DisableChildWindows(Handle);
}
