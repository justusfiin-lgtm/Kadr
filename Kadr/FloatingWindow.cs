using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Kadr.NativeMethods;

namespace Kadr;

/// <summary>
/// Безрамочное окно-спутник окна видео: панель управления или плейлист.
/// </summary>
/// <remarks>
/// Окно видео — не «владелец» (Owner) спутников: Windows всегда держит принадлежащие окна НАД владельцем,
/// а пользователь хочет, чтобы видео было выше (2026-10-24). Поэтому порядок окон держит сама программа:
/// пока <see cref="StayBelow"/> возвращает окно, этот спутник не может подняться выше него
/// (<see cref="WndProc"/> поправляет каждую попытку). Группу (поднять / свернуть вместе) ведёт окно видео.
/// Кнопки на панели задач у спутника нет (ShowInTaskbar = false).
/// </remarks>
public class FloatingWindow : Window
{
    public FloatingWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // Появляясь, окно не забирает клавиатуру у окна видео.
        ShowActivated = false;
        BorderThickness = new Thickness(1);
        FontFamily = new FontFamily("Segoe UI");
        SetResourceReference(BackgroundProperty, "PanelBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "WindowBorderBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
    }

    /// <summary>Окно видео этого экземпляра плеера: ему передаются горячие клавиши.</summary>
    internal VideoWindow? Host { get; set; }

    /// <summary>Под каким окном оставаться; IntPtr.Zero — сейчас можно быть выше него.</summary>
    internal Func<IntPtr>? StayBelow { get; set; }

    /// <summary>Пока false, закрытие окна (например, Alt+F4) только прячет его.
    /// Окно видео ставит true, когда закрывается вся программа.</summary>
    internal bool CloseForReal { get; set; }

    /// <summary>
    /// HWND окна; IntPtr.Zero — окно ещё ни разу не показывали.
    /// Заранее создавать его (EnsureHandle) нельзя: окно, созданное до показа, оставалось чёрным и пустым.
    /// </summary>
    internal IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(Hwnd)?.AddHook(WndProc);
    }

    /// <summary>Каждое перемещение окна «вверх» по стопке окон заменяем на «сразу под окном видео».</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING || StayBelow?.Invoke() is not { } below || below == IntPtr.Zero)
            return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        // HWND_TOPMOST/NOTOPMOST («поверх всех окон» вкл/выкл) пропускаем как есть — порядок
        // внутри группы окно видео восстановит сразу после этого.
        var changesOrder = (pos.flags & SWP_NOZORDER) == 0;
        var topmostSwitch = pos.hwndInsertAfter == HWND_TOPMOST || pos.hwndInsertAfter == HWND_NOTOPMOST;
        if (changesOrder && !topmostSwitch && pos.hwndInsertAfter != below)
        {
            pos.hwndInsertAfter = below;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Щёлкнули по панели или плейлисту, когда плеер был под другими программами, —
        // поднимаем всю группу, и видео тоже.
        Host?.BringGroupToFront();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Сюда доходят только нажатия на свободное место: кнопки, полосы и строки списка забирают их себе.
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Горячие клавиши работают, в каком бы из трёх окон ни был фокус.
        Host?.HandleKey(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!CloseForReal)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
