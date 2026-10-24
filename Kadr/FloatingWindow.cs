using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kadr;

/// <summary>
/// Безрамочное окно-спутник окна видео: панель управления или плейлист.
/// Владелец — окно видео, поэтому у всех трёх окон одна кнопка на панели задач
/// и они сворачиваются/разворачиваются вместе.
/// </summary>
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

    /// <summary>Пока false, закрытие окна (например, Alt+F4) только прячет его.
    /// Окно видео ставит true, когда закрывается вся программа.</summary>
    internal bool CloseForReal { get; set; }

    /// <summary>
    /// HWND окна; IntPtr.Zero — окно ещё ни разу не показывали.
    /// Заранее создавать его (EnsureHandle) нельзя: окно, созданное до показа, оставалось чёрным и пустым.
    /// </summary>
    internal IntPtr Hwnd => new WindowInteropHelper(this).Handle;

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
        (Owner as VideoWindow)?.HandleKey(e);
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
