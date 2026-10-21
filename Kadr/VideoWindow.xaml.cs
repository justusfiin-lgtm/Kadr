using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kadr.Mpv;
using static Kadr.NativeMethods;

namespace Kadr;

public partial class VideoWindow : Window
{
    /// <summary>Самое узкое окно видео (в единицах WPF, при масштабе 100% это пиксели).</summary>
    private const double MinVideoWidth = 320;

    /// <summary>Во сколько раз меняется окно за один «щелчок» колёсика.</summary>
    private const double WheelStep = 1.1;

    private readonly string _filePath;
    private readonly DispatcherTimer _singleClickTimer;
    private MpvPlayer? _player;

    /// <summary>Ширина картинки, делённая на высоту. До открытия видео — 16:9.</summary>
    private double _aspect = 16.0 / 9;

    /// <summary>Где нажали левую кнопку; null — кнопка не нажата или это уже перетаскивание.</summary>
    private Point? _pressPoint;

    /// <summary>Где было окно до полноэкранного режима; null — сейчас не полный экран.</summary>
    private RECT? _windowedBounds;

    public VideoWindow(string filePath)
    {
        InitializeComponent();
        // Ссылки (http://…, av://…) mpv открывает сам — путь на диске из них делать нельзя.
        _filePath = filePath.Contains("://") ? filePath : Path.GetFullPath(filePath);
        Title = $"{Path.GetFileName(_filePath)} — Kadr";

        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _singleClickTimer.Tick += (_, _) => OnSingleClick();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private bool IsFullscreen => _windowedBounds is not null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _player = new MpvPlayer(Video.Handle);
        }
        catch (DllNotFoundException)
        {
            ShowError("Не найден файл libmpv-2.dll. Он должен лежать в одной папке с Kadr.exe.");
            Close();
            return;
        }
        catch (MpvException ex)
        {
            ShowError(ex.Message);
            Close();
            return;
        }

        _player.EndFile += OnEndFile;
        _player.VideoReconfig += OnVideoReconfig;
        _player.Shutdown += Close;
        _player.LoadFile(_filePath);
    }

    private void OnEndFile(MpvEndFileReason reason, string? error)
    {
        if (reason == MpvEndFileReason.Error)
            ShowError($"Не удалось воспроизвести файл:\n{_filePath}\n\n{error}");
    }

    private void OnVideoReconfig()
    {
        // mpv мог только что создать своё окно для картинки — снова пускаем мышь сквозь него.
        Video.PassMouseThrough();

        // dwidth/dheight — размер картинки с учётом пропорций (в том числе у анаморфного видео).
        if (_player is null
            || !double.TryParse(_player.GetProperty("dwidth"), CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(_player.GetProperty("dheight"), CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
            return;

        _aspect = width / height;
        if (!IsFullscreen)
            ResizeAroundCenter(1);
    }

    // ---------- Мышь ----------
    // Щелчок без движения — пауза; с движением — перетаскивание окна; двойной щелчок — полный экран.

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            // Второй щелчок двойного: отменяем отложенный одиночный (паузу).
            _singleClickTimer.Stop();
            _pressPoint = null;
            ToggleFullscreen();
            return;
        }

        _pressPoint = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Мышь сдвинулась с зажатой кнопкой — это перетаскивание, а не щелчок.
        _pressPoint = null;
        ReleaseMouseCapture();
        if (!IsFullscreen)
            DragMove();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressPoint is null)
            return;

        _pressPoint = null;
        ReleaseMouseCapture();
        // Ждём, не придёт ли второй щелчок: если придёт — это двойной щелчок, а не пауза.
        _singleClickTimer.Start();
    }

    private void OnSingleClick()
    {
        _singleClickTimer.Stop();
        // В полноэкранном режиме щелчок будет показывать/скрывать панель управления —
        // появится вместе с окном панели (этап 2б). Пауза там — пробелом.
        if (!IsFullscreen)
            _player?.Command("cycle", "pause");
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (IsFullscreen)
            return;
        // Delta = 120 на один щелчок колёсика; тачпад присылает доли щелчка.
        ResizeAroundCenter(Math.Pow(WheelStep, e.Delta / 120.0));
    }

    // ---------- Клавиши (полный набор — на этапе 6) ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.System && e.SystemKey == Key.F10)
        {
            // F10 Windows считает «системной» клавишей (как Alt), поэтому она приходит так.
            Close();
            e.Handled = true;
        }
    }

    // ---------- Размер и полный экран ----------
    // Всё считаем в настоящих пикселях экрана: так проще с мониторами разного масштаба.

    /// <summary>
    /// Изменить ширину окна в <paramref name="factor"/> раз, сохранив центр и пропорции видео.
    /// С factor = 1 просто подгоняет окно под пропорции видео.
    /// </summary>
    private void ResizeAroundCenter(double factor)
    {
        var hwnd = Hwnd;
        var bounds = GetBounds(hwnd);
        var work = GetMonitorOf(hwnd).WorkArea;
        var minWidth = MinVideoWidth * VisualTreeHelper.GetDpi(this).DpiScaleX;

        var width = Math.Max(minWidth, Math.Min(bounds.Width * factor, work.Width));
        var height = width / _aspect;
        if (height > work.Height)
        {
            height = work.Height;
            width = height * _aspect;
        }

        var centerX = bounds.Left + bounds.Width / 2.0;
        var centerY = bounds.Top + bounds.Height / 2.0;
        SetBounds(hwnd,
            (int)Math.Round(centerX - width / 2), (int)Math.Round(centerY - height / 2),
            (int)Math.Round(width), (int)Math.Round(height));
    }

    /// <summary>Полный экран — на том мониторе, где сейчас окно.</summary>
    private void ToggleFullscreen()
    {
        var hwnd = Hwnd;
        if (_windowedBounds is { } saved)
        {
            _windowedBounds = null;
            BorderThickness = new Thickness(1);
            SetBounds(hwnd, saved.Left, saved.Top, saved.Width, saved.Height);
            // Пока был полный экран, могло открыться видео с другими пропорциями.
            ResizeAroundCenter(1);
        }
        else
        {
            _windowedBounds = GetBounds(hwnd);
            BorderThickness = new Thickness(0);
            var screen = GetMonitorOf(hwnd).Monitor;
            SetBounds(hwnd, screen.Left, screen.Top, screen.Width, screen.Height);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_player is null)
            return;

        // mpv рисует прямо в наше окно, поэтому его нужно остановить раньше, чем окно исчезнет.
        // Останавливаем в фоновом потоке, чтобы окно не «зависло», пока mpv завершается,
        // а потом закрываем окно уже по-настоящему.
        e.Cancel = true;
        var player = _player;
        _player = null;
        _singleClickTimer.Stop();
        Hide();
        await Task.Run(player.Dispose);
        Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Kadr", MessageBoxButton.OK, MessageBoxImage.Error);
}
