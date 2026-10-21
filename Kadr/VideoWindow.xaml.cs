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

/// <summary>
/// Окно видео — главное окно экземпляра плеера. Оно владеет окнами панели управления
/// и плейлиста: у всех трёх одна кнопка на панели задач, сворачиваются и закрываются они вместе.
/// </summary>
public partial class VideoWindow : Window
{
    /// <summary>Самое узкое окно видео (в единицах WPF, при масштабе 100% это пиксели).</summary>
    private const double MinVideoWidth = 320;

    /// <summary>Во сколько раз меняется окно за один «щелчок» колёсика.</summary>
    private const double WheelStep = 1.1;

    /// <summary>Промежуток между окнами в начальной раскладке.</summary>
    private const double WindowGap = 8;

    private readonly string _filePath;
    private readonly DispatcherTimer _singleClickTimer;
    private readonly ControlPanelWindow _panel = new();
    private readonly PlaylistWindow _playlist = new();
    private MpvPlayer? _player;

    /// <summary>Ширина картинки, делённая на высоту. До открытия видео — 16:9.</summary>
    private double _aspect = 16.0 / 9;

    /// <summary>Где нажали левую кнопку; null — кнопка не нажата или это уже перетаскивание.</summary>
    private Point? _pressPoint;

    /// <summary>Где было окно до полноэкранного режима; null — сейчас не полный экран.</summary>
    private RECT? _windowedBounds;

    /// <summary>Где были и видны ли панель и плейлист до полноэкранного режима.</summary>
    private SavedWindow[] _satellitesBeforeFullscreen = [];

    private readonly record struct SavedWindow(FloatingWindow Window, RECT Bounds, bool Visible);

    public VideoWindow(string filePath)
    {
        InitializeComponent();
        // Ссылки (http://…, av://…) mpv открывает сам — путь на диске из них делать нельзя.
        _filePath = filePath.Contains("://") ? filePath : Path.GetFullPath(filePath);
        Title = $"{Path.GetFileName(_filePath)} — Kadr";
        _playlist.Folder = Path.GetDirectoryName(_filePath);

        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _singleClickTimer.Tick += (_, _) => OnSingleClick();

        PlaceWindowsInitially();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private bool IsFullscreen => _windowedBounds is not null;

    /// <summary>
    /// Начальная раскладка (пока позиции не запоминаются — это этап 7): видео слева,
    /// под ним панель, справа плейлист; вся группа — по центру основного монитора.
    /// </summary>
    private void PlaceWindowsInitially()
    {
        var work = SystemParameters.WorkArea;
        var groupWidth = Width + WindowGap + _playlist.Width;
        var groupHeight = Math.Max(Height + WindowGap + _panel.Height, _playlist.Height);
        Left = work.Left + Math.Max(0, (work.Width - groupWidth) / 2);
        Top = work.Top + Math.Max(0, (work.Height - groupHeight) / 2);

        _panel.Left = Left;
        _panel.Top = Top + Height + WindowGap;
        _playlist.Left = Left + Width + WindowGap;
        _playlist.Top = Top;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _panel.Owner = this;
        _playlist.Owner = this;
        _panel.Show();
        _playlist.Show();

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

        _panel.Attach(_player);
        _panel.PlaylistClicked += () => ToggleVisible(_playlist);
        // «Предыдущий»/«Следующий» — этап 4 (плейлист), «Аудио»/«Субтитры» — этап 5.

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
            FitToAspect();
    }

    // ---------- Мышь ----------
    // Щелчок без движения — пауза (в полном экране — показать/скрыть панель);
    // с движением — перетаскивание окна; двойной щелчок — полный экран.

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            // Второй щелчок двойного: отменяем отложенный одиночный.
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
        // Ждём, не придёт ли второй щелчок: если придёт — это двойной щелчок, а не одиночный.
        _singleClickTimer.Start();
    }

    private void OnSingleClick()
    {
        _singleClickTimer.Stop();
        if (IsFullscreen)
            ToggleVisible(_panel);
        else
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        HandleKey(e);
    }

    /// <summary>Горячие клавиши всех трёх окон: панель и плейлист передают их сюда.</summary>
    internal void HandleKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        // F10 Windows считает «системной» клавишей (как Alt), поэтому она приходит как Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape when IsFullscreen:
                ToggleFullscreen();
                break;
            case Key.F10:
                Close();
                break;
            case Key.Tab:
                ToggleVisible(_panel);
                break;
            case Key.L:
                ToggleVisible(_playlist);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static void ToggleVisible(Window window)
    {
        if (window.IsVisible)
            window.Hide();
        else
            window.Show();
    }

    // ---------- Размер и полный экран ----------
    // Всё считаем в настоящих пикселях экрана: так проще с мониторами разного масштаба.

    /// <summary>Колёсико: изменить окно в <paramref name="factor"/> раз, сохранив центр и пропорции видео.</summary>
    private void ResizeAroundCenter(double factor)
    {
        var bounds = GetBounds(Hwnd);
        var (width, height) = FitSize(bounds.Width * factor);
        var centerX = bounds.Left + bounds.Width / 2.0;
        var centerY = bounds.Top + bounds.Height / 2.0;
        SetBounds(Hwnd,
            (int)Math.Round(centerX - width / 2), (int)Math.Round(centerY - height / 2),
            (int)Math.Round(width), (int)Math.Round(height));
    }

    /// <summary>Новое видео: подогнать высоту окна под его пропорции. Левый верхний угол остаётся на месте.</summary>
    private void FitToAspect()
    {
        var bounds = GetBounds(Hwnd);
        var (width, height) = FitSize(bounds.Width);
        SetBounds(Hwnd, bounds.Left, bounds.Top, (int)Math.Round(width), (int)Math.Round(height));
    }

    /// <summary>Размер окна с нужной шириной и пропорциями видео, не меньше минимума и не больше экрана.</summary>
    private (double Width, double Height) FitSize(double desiredWidth)
    {
        var work = GetMonitorOf(Hwnd).WorkArea;
        var minWidth = MinVideoWidth * VisualTreeHelper.GetDpi(this).DpiScaleX;

        var width = Math.Max(minWidth, Math.Min(desiredWidth, work.Width));
        var height = width / _aspect;
        if (height > work.Height)
        {
            height = work.Height;
            width = height * _aspect;
        }
        return (width, height);
    }

    /// <summary>
    /// Полный экран — на том мониторе, где сейчас окно. Панель и плейлист при входе прячутся,
    /// при выходе возвращаются туда, где были, и в том же виде (показаны или скрыты).
    /// </summary>
    private void ToggleFullscreen()
    {
        if (_windowedBounds is { } saved)
        {
            _windowedBounds = null;
            BorderThickness = new Thickness(1);
            SetBounds(Hwnd, saved.Left, saved.Top, saved.Width, saved.Height);
            // Пока был полный экран, могло открыться видео с другими пропорциями.
            FitToAspect();

            foreach (var (window, bounds, visible) in _satellitesBeforeFullscreen)
            {
                SetBounds(window.Hwnd, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                if (visible)
                    window.Show();
                else
                    window.Hide();
            }
        }
        else
        {
            _satellitesBeforeFullscreen =
            [
                new(_panel, GetBounds(_panel.Hwnd), _panel.IsVisible),
                new(_playlist, GetBounds(_playlist.Hwnd), _playlist.IsVisible),
            ];
            _panel.Hide();
            _playlist.Hide();

            _windowedBounds = GetBounds(Hwnd);
            BorderThickness = new Thickness(0);
            var screen = GetMonitorOf(Hwnd).Monitor;
            SetBounds(Hwnd, screen.Left, screen.Top, screen.Width, screen.Height);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_player is null)
        {
            CloseSatellites();
            return;
        }

        // mpv рисует прямо в наше окно, поэтому его нужно остановить раньше, чем окно исчезнет.
        // Останавливаем в фоновом потоке, чтобы окно не «зависло», пока mpv завершается,
        // а потом закрываем окно уже по-настоящему.
        e.Cancel = true;
        var player = _player;
        _player = null;
        _panel.Detach();
        _singleClickTimer.Stop();
        Hide();
        _panel.Hide();
        _playlist.Hide();
        await Task.Run(player.Dispose);
        Close();
    }

    private void CloseSatellites()
    {
        _panel.CloseForReal = true;
        _playlist.CloseForReal = true;
        _panel.Close();
        _playlist.Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Kadr", MessageBoxButton.OK, MessageBoxImage.Error);
}
