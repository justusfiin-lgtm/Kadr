using System.ComponentModel;
using System.Diagnostics;
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
/// Окно видео — главное окно экземпляра плеера. Панель управления и плейлист — его спутники:
/// кнопка на панели задач одна (у окна видео), поднимаются, сворачиваются и закрываются они вместе.
/// Окно видео всегда выше спутников (кроме полного экрана и панели, поднятой клавишей H) —
/// см. <see cref="FloatingWindow.StayBelow"/> и <see cref="BringGroupToFront"/>.
/// </summary>
public partial class VideoWindow : Window
{
    /// <summary>Самое узкое окно видео (в единицах WPF, при масштабе 100% это пиксели).</summary>
    private const double MinVideoWidth = 320;

    /// <summary>Во сколько раз меняется окно за один «щелчок» колёсика.</summary>
    private const double WheelStep = 1.1;

    /// <summary>Промежуток между окнами в начальной раскладке.</summary>
    private const double WindowGap = 8;

    /// <summary>На сколько пикселей сдвигать окна нового плеера, если уже открыт другой.</summary>
    private const int InstanceOffset = 30;

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

    /// <summary>Настройки, прочитанные при запуске: положения окон, громкость.</summary>
    private readonly Settings _settings = Storage.LoadSettings();

    /// <summary>Текущий файл уже загрузился — можно читать его время (для «продолжить с места»).</summary>
    private bool _fileLoaded;

    /// <summary>С какой секунды продолжили текущий файл — для надписи; null — с начала.</summary>
    private double? _resumedFrom;

    /// <summary>Окно-спутник до полного экрана; Bounds = null — его ещё ни разу не показывали.</summary>
    private readonly record struct SavedWindow(FloatingWindow Window, RECT? Bounds, bool Visible);

    public VideoWindow(string filePath)
    {
        InitializeComponent();
        _startPath = filePath;
        _playlistWindow.Attach(_playlist);
        _playlistWindow.PlayRequested += Play;

        _panel.Host = this;
        _playlistWindow.Host = this;
        // Спутники держатся под окном видео; панель может быть выше в полном экране и после H,
        // плейлист — в полном экране.
        _panel.StayBelow = () => IsFullscreen || _panelAboveVideo ? IntPtr.Zero : Hwnd;
        _playlistWindow.StayBelow = () => IsFullscreen ? IntPtr.Zero : Hwnd;
        Activated += (_, _) => BringGroupToFront();
        StateChanged += OnStateChanged;

        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _singleClickTimer.Tick += (_, _) => OnSingleClick();

        PlaceWindowsInitially();
        SourceInitialized += (_, _) => RestorePlacement();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>Панель поднята над окном видео клавишей H — до повторного H или щелчка по видео.</summary>
    private bool _panelAboveVideo;

    /// <summary>Какие спутники были видны перед сворачиванием окна видео; null — окно не свёрнуто.</summary>
    private (bool Panel, bool Playlist)? _visibleBeforeMinimize;

    private bool IsFullscreen => _windowedBounds is not null;

    /// <summary>
    /// Раскладка по умолчанию — для первого запуска (и для окна, чьё сохранённое место оказалось
    /// за пределами экранов): видео слева, под ним панель, справа плейлист; группа — по центру
    /// основного монитора.
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

    /// <summary>
    /// Поставить все три окна туда, где они были при последнем закрытии (CLAUDE.md, раздел 11).
    /// Если уже открыт другой плеер — со сдвигом вниз-вправо, чтобы не лечь ровно поверх (раздел 10).
    /// Окно видео ставится, когда у него уже есть HWND, но оно ещё не показано; панель и плейлист —
    /// так же, в момент их создания при первом показе. Поэтому окна не «прыгают» на экране.
    /// </summary>
    private void RestorePlacement()
    {
        var otherPlayers = Process.GetProcessesByName("Kadr").Count(p => p.Id != Environment.ProcessId);
        var offset = InstanceOffset * Math.Min(otherPlayers, 5);

        Place(Hwnd, _settings.VideoWindow);
        _panel.SourceInitialized += (_, _) => Place(_panel.Hwnd, _settings.PanelWindow);
        _playlistWindow.SourceInitialized += (_, _) => Place(_playlistWindow.Hwnd, _settings.PlaylistWindow);

        void Place(IntPtr hwnd, WindowPlacement? saved)
        {
            var bounds = GetBounds(hwnd);
            if (saved is { Width: > 0, Height: > 0 }
                && IsOnAnyMonitor(saved.Left, saved.Top, saved.Width, saved.Height))
                bounds = new RECT
                {
                    Left = saved.Left, Top = saved.Top,
                    Right = saved.Left + saved.Width, Bottom = saved.Top + saved.Height,
                };
            SetBounds(hwnd, bounds.Left + offset, bounds.Top + offset, bounds.Width, bounds.Height);
        }
    }

    /// <summary>Запомнить положения окон и громкость — при закрытии («последний закрытый побеждает»).</summary>
    private void SaveSettings()
    {
        double? volume = null;
        if (_player is { } player)
            MpvPlayer.Try(() => volume = ParseNumber(player.GetProperty("volume")));

        // Свёрнутое окно стоит «за экраном» (−32000) — такие места не запоминаем.
        var saveWindows = WindowState != WindowState.Minimized;
        var video = _windowedBounds ?? GetBounds(Hwnd);
        var panel = SatelliteState(_panel);
        var playlist = SatelliteState(_playlistWindow);

        Storage.UpdateSettings(settings =>
        {
            if (volume is { } v)
                settings.Volume = v;
            if (!saveWindows)
                return;
            settings.VideoWindow = ToPlacement(video, true);
            settings.PanelWindow = Merge(settings.PanelWindow, panel);
            settings.PlaylistWindow = Merge(settings.PlaylistWindow, playlist);
        });

        static WindowPlacement ToPlacement(RECT r, bool visible) =>
            new() { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Visible = visible };

        // Окно, которое в этот раз ни разу не показывали, остаётся на прежнем месте (и скрытым).
        static WindowPlacement? Merge(WindowPlacement? old, (RECT? Bounds, bool Visible) now) =>
            now.Bounds is { } bounds ? ToPlacement(bounds, now.Visible)
            : old is null ? null
            : new() { Left = old.Left, Top = old.Top, Width = old.Width, Height = old.Height, Visible = false };
    }

    /// <summary>
    /// Где окно-спутник и видно ли оно; в полном экране — каким было до него.
    /// Bounds = null — окно ещё ни разу не показывали.
    /// </summary>
    private (RECT? Bounds, bool Visible) SatelliteState(FloatingWindow window)
    {
        if (IsFullscreen)
            foreach (var saved in _satellitesBeforeFullscreen)
                if (saved.Window == window)
                    return (saved.Bounds, saved.Visible);
        return window.Hwnd == IntPtr.Zero ? (null, false) : (GetBounds(window.Hwnd), window.IsVisible);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.PanelWindow?.Visible ?? true)
            _panel.Show();
        if (_settings.PlaylistWindow?.Visible ?? true)
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
        _player.FileLoaded += OnFileLoaded;

        // Громкость — общая для всех плееров, такая же, как при последнем закрытии.
        MpvPlayer.Try(() => _player.SetProperty("volume",
            _settings.Volume.ToString(CultureInfo.InvariantCulture)));
        // core-idle = «сейчас ничего не играет» (пауза, конец файла…) — для запрета сна.
        _player.ObserveProperty("core-idle", MpvFormat.Flag);

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
        SaveResumePosition();

        _playlist.Current = item;
        Title = $"{item.Name} — Kadr";
        _fileLoaded = false;
        // Этот файл уже начинали смотреть — продолжаем с того же места.
        _resumedFrom = IsUrl(item.Path) ? null : Storage.ResumePosition(item.Path);
        _player.LoadFile(item.Path, _resumedFrom);
    }

    private void OnFileLoaded()
    {
        _fileLoaded = true;
        // Имя файла в углу видео; если продолжили — и с какого места.
        var name = _playlist.Current?.Name ?? "";
        _actions?.Osd(_resumedFrom is { } from
            ? $"{name}\nПродолжение с {ControlPanelWindow.FormatTime(from)}"
            : name);
    }

    /// <summary>
    /// Запомнить, где остановился просмотр текущего файла (CLAUDE.md, раздел 11).
    /// Вызывается перед сменой файла и при закрытии программы.
    /// </summary>
    private void SaveResumePosition()
    {
        if (_player is not { } player || !_fileLoaded || _playlist.Current is not { } item || IsUrl(item.Path))
            return;
        double? position = null, duration = null;
        MpvPlayer.Try(() =>
        {
            position = ParseNumber(player.GetProperty("time-pos"));
            duration = ParseNumber(player.GetProperty("duration"));
        });
        if (position is { } p)
            Storage.SaveResumePosition(item.Path, p, duration ?? 0);
    }

    private static bool IsUrl(string path) => path.Contains("://");

    private static double? ParseNumber(string? text) =>
        double.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : null;

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
        switch (name)
        {
            // Файл досмотрен — следующий по списку. В конце списка плеер стоит на последнем кадре.
            case "eof-reached" when value is true:
                if (_playlist.Next is { } next)
                    Play(next);
                else
                    SaveResumePosition(); // досмотрен до конца — место остановки забывается
                break;

            // Пока видео играет, компьютер не засыпает и не гасит экран (раздел 5).
            case "core-idle":
                KeepAwake(value is false);
                break;
        }
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
    // Щелчок без движения: в оконном режиме — ничего (паузу по щелчку пользователь попросил убрать,
    // 2026-10-24), в полном экране — показать/скрыть панель; с движением — перетаскивание окна;
    // двойной щелчок — полный экран.

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Щелчок по видео опускает панель, поднятую клавишей H, обратно под видео.
        if (_panelAboveVideo)
        {
            _panelAboveVideo = false;
            BringGroupToFront();
        }

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
        if (IsFullscreen)
            _singleClickTimer.Start();
    }

    private void OnSingleClick()
    {
        _singleClickTimer.Stop();
        if (IsFullscreen)
            ToggleVisible(_panel);
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
            case Key.H:
                TogglePanelAboveVideo();
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

    /// <summary>
    /// Ctrl+A: все три окна этого экземпляра поверх всех окон Windows (или обратно).
    /// Внутри группы порядок прежний: видео выше спутников.
    /// </summary>
    private void ToggleTopmost()
    {
        var on = !Topmost;
        Topmost = on;
        _panel.Topmost = on;
        _playlistWindow.Topmost = on;
        BringGroupToFront();
        _actions?.Osd(on ? "Поверх всех окон: вкл" : "Поверх всех окон: выкл");
    }

    /// <summary>H: поднять панель над окном видео (если скрыта — показать) или опустить обратно.</summary>
    private void TogglePanelAboveVideo()
    {
        _panelAboveVideo = !_panelAboveVideo || !_panel.IsVisible;
        if (!_panel.IsVisible)
            _panel.Show();
        BringGroupToFront();
    }

    private void ToggleVisible(FloatingWindow window)
    {
        if (window.IsVisible)
        {
            window.Hide();
            return;
        }
        window.Show();
        BringGroupToFront();
    }

    /// <summary>
    /// Поднять группу окон наверх: окно видео — над всеми, спутники — сразу под ним
    /// (или над ним, если им сейчас можно: полный экран, панель после H). Фокус не трогается.
    /// </summary>
    internal void BringGroupToFront()
    {
        var video = Hwnd;
        if (video == IntPtr.Zero || WindowState == WindowState.Minimized)
            return;

        SetZOrder(video, HWND_TOP);
        foreach (var window in new FloatingWindow[] { _playlistWindow, _panel })
        {
            if (window.Hwnd == IntPtr.Zero || !window.IsVisible)
                continue;
            // Если спутнику сейчас можно быть выше видео — наверх; иначе его WndProc сам
            // поставит его сразу под видео.
            SetZOrder(window.Hwnd, window.StayBelow?.Invoke() == IntPtr.Zero ? HWND_TOP : video);
        }
    }

    /// <summary>Окно видео свернули — спутники прячутся; развернули — возвращаются, какими были.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _visibleBeforeMinimize = (_panel.IsVisible, _playlistWindow.IsVisible);
            _panel.Hide();
            _playlistWindow.Hide();
            return;
        }

        if (_visibleBeforeMinimize is { } visible)
        {
            _visibleBeforeMinimize = null;
            if (visible.Panel)
                _panel.Show();
            if (visible.Playlist)
                _playlistWindow.Show();
            BringGroupToFront();
        }
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
            _panelAboveVideo = false;
            BorderThickness = new Thickness(1);
            SetBounds(Hwnd, saved.Left, saved.Top, saved.Width, saved.Height);
            // Пока был полный экран, могло открыться видео с другими пропорциями.
            FitToAspect();

            foreach (var (window, bounds, visible) in _satellitesBeforeFullscreen)
            {
                if (bounds is { } b)
                    SetBounds(window.Hwnd, b.Left, b.Top, b.Width, b.Height);
                if (visible)
                    window.Show();
                else
                    window.Hide();
            }
            BringGroupToFront();
        }
        else
        {
            _satellitesBeforeFullscreen =
            [
                Save(_panel),
                Save(_playlistWindow),
            ];
            _panel.Hide();
            _playlistWindow.Hide();

            _windowedBounds = GetBounds(Hwnd);
            BorderThickness = new Thickness(0);
            var screen = GetMonitorOf(Hwnd).Monitor;
            SetBounds(Hwnd, screen.Left, screen.Top, screen.Width, screen.Height);
        }

        static SavedWindow Save(FloatingWindow window) =>
            window.Hwnd == IntPtr.Zero
                ? new(window, null, false)
                : new(window, GetBounds(window.Hwnd), window.IsVisible);
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
        // Пока плеер жив — запоминаем место просмотра, громкость и положения окон.
        SaveResumePosition();
        SaveSettings();
        KeepAwake(false);

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

    /// <summary>Не давать Windows усыплять компьютер и гасить экран (true) или снова разрешить (false).</summary>
    private static void KeepAwake(bool on) =>
        SetThreadExecutionState(on
            ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
            : ES_CONTINUOUS);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Kadr", MessageBoxButton.OK, MessageBoxImage.Error);
}
