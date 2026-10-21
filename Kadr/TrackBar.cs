using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Kadr;

/// <summary>
/// Полоса с заполнением: перемотка и громкость. Щелчок в любое место — переход туда,
/// перетаскивание с зажатой кнопкой — плавное изменение. Мышь «попадает» по всей высоте
/// элемента, а не только по видимой дорожке.
/// </summary>
public class TrackBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0);
    public static readonly DependencyProperty TrackThicknessProperty = Register(nameof(TrackThickness), 4.0);
    public static readonly DependencyProperty ThumbSizeProperty = Register(nameof(ThumbSize), 0.0);
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush?>(nameof(TrackBrush), null);
    public static readonly DependencyProperty FillBrushProperty = Register<Brush?>(nameof(FillBrush), null);
    public static readonly DependencyProperty ThumbBrushProperty = Register<Brush?>(nameof(ThumbBrush), null);

    public TrackBar()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    /// <summary>Положение от 0 (начало) до 1 (конец).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, 1));
    }

    /// <summary>Толщина видимой дорожки; концы скруглены на половину толщины.</summary>
    public double TrackThickness
    {
        get => (double)GetValue(TrackThicknessProperty);
        set => SetValue(TrackThicknessProperty, value);
    }

    /// <summary>Диаметр круглого бегунка; 0 — без бегунка.</summary>
    public double ThumbSize
    {
        get => (double)GetValue(ThumbSizeProperty);
        set => SetValue(ThumbSizeProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? ThumbBrush
    {
        get => (Brush?)GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    /// <summary>Пользователь сейчас тянет полосу — внешние обновления (время, громкость) не должны ей мешать.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>Значение меняется мышью (щелчок или перетаскивание).</summary>
    public event Action<double>? Dragging;

    /// <summary>Кнопку мыши отпустили — окончательное значение.</summary>
    public event Action<double>? Committed;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        // Прозрачный фон на всю высоту, чтобы щелчок рядом с тонкой дорожкой тоже попадал.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var thickness = TrackThickness;
        var radius = thickness / 2;
        var top = (height - thickness) / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, top, width, thickness), radius, radius);

        if (!IsEnabled)
            return;

        var filled = PositionOf(Value);
        if (filled > 0)
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, top, filled, thickness), radius, radius);
        if (ThumbSize > 0)
            dc.DrawEllipse(ThumbBrush, null, new Point(filled, height / 2), ThumbSize / 2, ThumbSize / 2);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Нажатие на полосу не должно начинать перетаскивание окна.
        e.Handled = true;
        CaptureMouse();
        IsDragging = true;
        UpdateFromMouse(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDragging)
            UpdateFromMouse(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsDragging)
            return;
        e.Handled = true;
        IsDragging = false;
        ReleaseMouseCapture();
        Committed?.Invoke(Value);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Захват мыши потерян посреди перетаскивания (например, Alt+Tab) — считаем, что отпустили.
        if (!IsDragging)
            return;
        IsDragging = false;
        Committed?.Invoke(Value);
    }

    private void UpdateFromMouse(MouseEventArgs e)
    {
        var (start, length) = Travel();
        if (length <= 0)
            return;
        Value = (e.GetPosition(this).X - start) / length;
        Dragging?.Invoke(Value);
    }

    /// <summary>Где по горизонтали находится значение <paramref name="value"/>.</summary>
    private double PositionOf(double value)
    {
        var (start, length) = Travel();
        return start + length * value;
    }

    /// <summary>
    /// Путь бегунка: он не выходит за края дорожки (иначе в конце полосы наезжал бы на цифры времени).
    /// Без бегунка — вся ширина.
    /// </summary>
    private (double Start, double Length) Travel()
    {
        var inset = Math.Min(ThumbSize / 2, ActualWidth / 2);
        return (inset, ActualWidth - 2 * inset);
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(TrackBar),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
