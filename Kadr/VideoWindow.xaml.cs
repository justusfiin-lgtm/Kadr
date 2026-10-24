using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>Файл, с которым запустили программу.</summary>
    private readonly string _startPath;
    private readonly DispatcherTimer _singleClickTimer;
    private readonly ControlPanelWindow _panel = new();
    private readonly PlaylistWindow _playlistWindow = new();
    private readonly Playlist _playlist = new();
    private MpvPlayer? _player;
    private TrackSwitcher? _tracks;
    private PlaybackActions? _actions;

    /// <summary>Шаг сдвига субтитров и звука, секунды.</summary>
    private const double SyncStep = 0.1;

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
        _startPath = filePath;
        _playlistWindow.Attach(_playlist);
        _playlistWindow.PlayRequested += Play;

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
        var groupWidth = Width + WindowGap + _playlistWindow.Width;
        var groupHeight = Math.Max(Height + WindowGap + _panel.Height, _playlistWindow.Height);
        Left = work.Left + Math.Max(0, (work.Width - groupWidth) / 2);
        Top = work.Top + Math.Max(0, (work.Height - groupHeight) / 2);

        _panel.Left = Left;
        _panel.Top = Top + Height + WindowGap;
        _playlistWindow.Left = Left + Width + WindowGap;
        _playlistWindow.Top = Top;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _panel.Owner = this;
        _playlistWindow.Owner = this;
        _panel.Show();
        _playlistWindow.Show();

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
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.Shutdown += Close;
        _player.ObserveProperty("eof-reached", MpvFormat.Flag);

        _panel.Attach(_player);
        _panel.PlaylistClicked += () => ToggleVisible(_playlistWindow);
        _panel.PreviousClicked += () => Play(_playlist.PreviousInCircle);
        _panel.NextClicked += () => Play(_playlist.NextInCircle);
        _panel.CloseProgramClicked += Close;

        _tracks = new TrackSwitcher(_player);
        _panel.AudioClicked += ShowAudioMenu;
        _panel.SubtitlesClicked += ShowSubtitlesMenu;

        _actions = new PlaybackActions(_player);
        // При смене файла — его имя в углу видео.
        _player.FileLoaded += () => _actions?.Osd(_playlist.Current?.Name ?? "");

        OpenFile(_startPath);
    }

    // ---------- Что играет ----------

    /// <summary>
    /// Открыть один файл: плейлист заменяется всеми видео из его папки (без подпапок),
    /// играет именно этот файл (CLAUDE.md, раздел 6.1).
    /// </summary>
    private void OpenFile(string path)
    {
        // Ссылки (http://…, av://…) mpv открывает сам — папки у них нет.
        if (path.Contains("://"))
        {
            _playlist.Replace([path]);
            Play(_playlist.Items[0]);
            return;
        }

        path = Path.GetFullPath(path);
        _playlist.Replace(VideoFiles.FolderOf(path));
        Play(_playlist.Find(path));
    }

    /// <summary>Открыть папку целиком (со всеми подпапками) и играть с первого файла.</summary>
    private void OpenFolder(string folder)
    {
        _playlist.Replace(VideoFiles.FolderTree(folder));
        Play(_playlist.Items.FirstOrDefault());
    }

    private void Play(PlaylistItem? item)
    {
        if (item is null || _player is null)
            return;
        _playlist.Current = item;
        Title = $"{item.Name} — Kadr";
        _player.LoadFile(item.Path);
    }

    // ---------- Меню «Аудио» и «Субтитры» (раздел 4.5): дорожки, у выбранной — галочка ----------

    private void ShowAudioMenu(FrameworkElement button)
    {
        if (_tracks is not { } tracks)
            return;
        var menu = new ContextMenu();
        var audio = tracks.AudioTracks();
        if (audio.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Звуковых дорожек нет", IsEnabled = false });
        foreach (var track in audio)
            menu.Items.Add(TrackItem(track.Label, track.Selected, () => tracks.SelectAudio(track)));
        OpenAbove(menu, button);
    }

    private void ShowSubtitlesMenu(FrameworkElement button)
    {
        if (_tracks is not { } tracks)
            return;
        var menu = new ContextMenu();
        var subtitles = tracks.SubtitleTracks();
        foreach (var track in subtitles)
            menu.Items.Add(TrackItem(track.Label, track.Selected, () => tracks.SelectSubtitles(track)));
        if (subtitles.Count > 0)
            menu.Items.Add(new Separator());
        menu.Items.Add(TrackItem("Выкл", !subtitles.Any(t => t.Selected), () => tracks.SelectSubtitles(null)));
        OpenAbove(menu, button);
    }

    private static MenuItem TrackItem(string label, bool isChecked, Action onClick)
    {
        // Подпись — в TextBlock: иначе «_» в названии дорожки WPF принял бы за подчёркнутую букву.
        var item = new MenuItem { Header = new TextBlock { Text = label }, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void OpenAbove(ContextMenu menu, FrameworkElement button)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnPlayerPropertyChanged(string name, object? value)
    {
        // Файл досмотрен — следующий по списку. В конце списка плеер стоит на последнем кадре.
        if (name == "eof-reached" && value is true)
            Play(_playlist.Next);
    }

    private void OnEndFile(MpvEndFileReason reason, string? error)
    {
        if (reason == MpvEndFileReason.Error)
            ShowError($"Не удалось воспроизвести файл:\n{_playlist.Current?.Path}\n\n{error}");
    }

    // ---------- Перетаскивание файла на окно видео: открыть его папку ----------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;
        var first = paths[0];
        if (Directory.Exists(first))
            OpenFolder(first);
        else if (File.Exists(first))
            OpenFile(first);
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

    // ---------- Горячие клавиши (CLAUDE.md, раздел 8; справка — «Горячие клавиши.txt») ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        HandleKey(e);
    }

    /// <summary>
    /// Горячие клавиши всех трёх окон: панель и плейлист передают их сюда ещё до того,
    /// как их получит кнопка или список (поэтому стрелки и пробел в плейлисте — тоже клавиши плеера).
    /// </summary>
    internal void HandleKey(KeyEventArgs e)
    {
        // F10 Windows считает «системной» клавишей (как Alt), поэтому она приходит как Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Клавиши проверяем по Key — это физическая клавиша, поэтому в русской раскладке
        // S — это и «Ы», «,» — «Б», «[» — «Х» и т. д.
        // С Shift работают только S, Z, X; с Ctrl — только A.
        var modifiers = Keyboard.Modifiers;
        var shift = modifiers == ModifierKeys.Shift;
        var allowed = modifiers == ModifierKeys.None
            || (shift && key is Key.S or Key.Z or Key.X)
            || (modifiers == ModifierKeys.Control && key == Key.A);
        if (!allowed)
            return;

        switch (key)
        {
            // Воспроизведение
            case Key.Space:
                _actions?.TogglePause();
                break;
            case Key.OemComma:
                _actions?.Seek(-PlaybackActions.LongSeek);
                break;
            case Key.OemPeriod:
                _actions?.Seek(+PlaybackActions.LongSeek);
                break;
            case Key.Left:
                _actions?.Seek(-PlaybackActions.ShortSeek);
                break;
            case Key.Right:
                _actions?.Seek(+PlaybackActions.ShortSeek);
                break;
            case Key.OemOpenBrackets:
                _actions?.ChangeSpeed(-PlaybackActions.SpeedStep);
                break;
            case Key.OemCloseBrackets:
                _actions?.ChangeSpeed(+PlaybackActions.SpeedStep);
                break;
            case Key.Back:
                _actions?.ResetSpeed();
                break;

            // Звук
            case Key.Up:
                _actions?.ChangeVolume(+PlaybackActions.VolumeStep);
                break;
            case Key.Down:
                _actions?.ChangeVolume(-PlaybackActions.VolumeStep);
                break;
            case Key.M:
                _actions?.ToggleMute();
                break;
            case Key.A when modifiers == ModifierKeys.Control:
                ToggleTopmost();
                break;
            case Key.A:
                _tracks?.NextAudio();
                break;
            case Key.Z when shift:
                _tracks?.ShiftAudio(-SyncStep);
                break;
            case Key.X when shift:
                _tracks?.ShiftAudio(+SyncStep);
                break;

            // Субтитры
            case Key.S when shift:
                _tracks?.NextSubtitles();
                break;
            case Key.S:
                _tracks?.ToggleSubtitles();
                break;
            case Key.Z:
                _tracks?.ShiftSubtitles(-SyncStep);
                break;
            case Key.X:
                _tracks?.ShiftSubtitles(+SyncStep);
                break;

            // Плейлист
            case Key.B:
            case Key.PageDown:
                Play(_playlist.NextInCircle);
                break;
            case Key.V:
            case Key.PageUp:
                Play(_playlist.PreviousInCircle);
                break;
            case Key.L:
                ToggleVisible(_playlistWindow);
                break;

            // Окна и экран
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.Escape when IsFullscreen:
                ToggleFullscreen();
                break;
            case Key.Tab:
                ToggleVisible(_panel);
                break;
            case Key.P:
                _actions?.Screenshot(_playlist.Current);
                break;
            case Key.F10:
                Close();
                break;

            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Ctrl+A: все три окна этого экземпляра поверх остальных окон (или обратно).</summary>
    private void ToggleTopmost()
    {
        var on = !Topmost;
        Topmost = on;
        _panel.Topmost = on;
        _playlistWindow.Topmost = on;
        _actions?.Osd(on ? "Поверх всех окон: вкл" : "Поверх всех окон: выкл");
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
                new(_playlistWindow, GetBounds(_playlistWindow.Hwnd), _playlistWindow.IsVisible),
            ];
            _panel.Hide();
            _playlistWindow.Hide();

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
        _tracks = null;
        _actions = null;
        _panel.Detach();
        _singleClickTimer.Stop();
        Hide();
        _panel.Hide();
        _playlistWindow.Hide();
        await Task.Run(player.Dispose);
        Close();
    }

    private void CloseSatellites()
    {
        _panel.CloseForReal = true;
        _playlistWindow.CloseForReal = true;
        _panel.Close();
        _playlistWindow.Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Kadr", MessageBoxButton.OK, MessageBoxImage.Error);
}
