using System.Runtime.InteropServices;

namespace Kadr;

/// <summary>Функции Windows (user32.dll), которых нет в WPF.</summary>
internal static class NativeMethods
{
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int SS_BLACKRECT = 0x00000004;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Границы окна в настоящих пикселях экрана (не в единицах WPF).</summary>
    public static RECT GetBounds(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return rect;
    }

    /// <summary>Поставить окно в заданное место и размер (в пикселях экрана).</summary>
    public static void SetBounds(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Весь экран и рабочая область (без панели задач) монитора, где сейчас окно.</summary>
    public static (RECT Monitor, RECT WorkArea) GetMonitorOf(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
        return (info.rcMonitor, info.rcWork);
    }

    /// <summary>Отключить все дочерние окна: щелчки мыши по ним уйдут родителю.</summary>
    public static void DisableChildWindows(IntPtr parent) =>
        EnumChildWindows(parent, (child, _) =>
        {
            EnableWindow(child, false);
            return true;
        }, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    /// <summary>Сравнение имён файлов как в проводнике («Серия 2» раньше «Серия 10»).</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string x, string y);

    /// <summary>Сколько миллисекунд Windows ждёт второго щелчка, чтобы засчитать двойной.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hwnd, bool enable);
}
