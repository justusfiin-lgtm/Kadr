using System.Globalization;
using System.Windows;
using Kadr.Mpv;

namespace Kadr;

/// <summary>
/// Панель управления. Сама управляет воспроизведением (пауза, стоп, перемотка, громкость),
/// а то, что касается других окон и плейлиста, отдаёт окну видео через события.
/// </summary>
public partial class ControlPanelWindow : FloatingWindow
{
    private MpvPlayer? _player;

    /// <summary>Длительность файла в секундах; 0 — неизвестна (файл не открыт).</summary>
    private double _duration;

    /// <summary>
    /// Куда перематываем (секунды) и когда начали; null — перемотки нет. Пока mpv перематывает, он ещё
    /// сообщает старое время — бегунок в это время не трогаем, иначе он отскакивает назад и обратно.
    /// </summary>
    private (double Target, DateTime Since)? _pendingSeek;

    /// <summary>Сколько ждать, пока время плеера дойдёт до места перемотки, прежде чем снова его показывать.</summary>
    private static readonly TimeSpan SeekSettleTime = TimeSpan.FromSeconds(1.5);

    public ControlPanelWindow()
    {
        InitializeComponent();

        // Тянут бегунок — быстрая перемотка по опорным кадрам (видео успевает за мышью); отпустили или
        // просто щёлкнули — одна точная перемотка в выбранное место. Если делать при щелчке обе сразу,
        // картинка дёргается: сначала мелькает ближайший опорный кадр, потом нужный.
        SeekBar.Dragging += value => Seek(value, "absolute+keyframes");
        SeekBar.Committed += value => Seek(value, "absolute");
        VolumeBar.Dragging += SetVolume;
        VolumeBar.Committed += SetVolume;
    }

    public event Action? PreviousClicked;
    public event Action? NextClicked;
    /// <summary>Нажата кнопка «Аудио»; передаётся сама кнопка — меню открывается над ней.</summary>
    public event Action<FrameworkElement>? AudioClicked;

    /// <summary>Нажата кнопка «Субтитры»; передаётся сама кнопка — меню открывается над ней.</summary>
    public event Action<FrameworkElement>? SubtitlesClicked;
    public event Action? PlaylistClicked;

    /// <summary>Нажат крестик «Закрыть программу».</summary>
    public event Action? CloseProgramClicked;

    /// <summary>Подключить панель к плееру: она начинает показывать его время, паузу и громкость.</summary>
    internal void Attach(MpvPlayer player)
    {
        _player = player;
        player.PropertyChanged += OnPlayerPropertyChanged;
        player.ObserveProperty("time-pos", MpvFormat.Double);
        player.ObserveProperty("duration", MpvFormat.Double);
        player.ObserveProperty("pause", MpvFormat.Flag);
        player.ObserveProperty("volume", MpvFormat.Double);
        player.ObserveProperty("mute", MpvFormat.Flag);
    }

    /// <summary>Отключить от плеера перед его остановкой — чтобы панель больше ничего ему не посылала.</summary>
    internal void Detach()
    {
        if (_player is not null)
            _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player = null;
    }

    private void OnPlayerPropertyChanged(string name, object? value)
    {
        switch (name)
        {
            case "time-pos":
                var position = value as double? ?? 0;
                // Пока пользователь тянет полосу или mpv ещё перематывает, бегунок и время
                // показывают выбранное место, а не старое время плеера.
                if (SeekBar.IsDragging || IsSeekPending(position))
                    break;
                ElapsedText.Text = FormatTime(position);
                SeekBar.Value = _duration > 0 ? position / _duration : 0;
                break;

            case "duration":
                _duration = value as double? ?? 0;
                DurationText.Text = FormatTime(_duration);
                SeekBar.IsEnabled = _duration > 0;
                break;

            case "pause":
                var paused = value as bool? ?? false;
                PauseIcon.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
                PlayIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
                PlayPauseButton.ToolTip = paused ? "Воспроизведение (Пробел)" : "Пауза (Пробел)";
                break;

            case "volume":
                if (!VolumeBar.IsDragging)
                    VolumeBar.Value = (value as double? ?? 0) / 100;
                break;

            case "mute":
                var muted = value as bool? ?? false;
                SoundWave.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
                MutedCross.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
                MuteButton.ToolTip = muted ? "Включить звук (M)" : "Выключить звук (M)";
                break;
        }
    }

    private void Seek(double fraction, string mode)
    {
        if (_duration <= 0)
            return;
        var target = fraction * _duration;
        ElapsedText.Text = FormatTime(target);
        _pendingSeek = (target, DateTime.UtcNow);
        Send(player => player.Command("seek", target.ToString("0.###", CultureInfo.InvariantCulture), mode));
    }

    /// <summary>Время плеера ещё не дошло до места перемотки (и прошло не слишком много времени).</summary>
    private bool IsSeekPending(double position)
    {
        if (_pendingSeek is not { } seek)
            return false;
        // Опорный кадр может быть на несколько секунд раньше цели — «дошли» считаем с запасом.
        if (Math.Abs(position - seek.Target) < 1 || DateTime.UtcNow - seek.Since > SeekSettleTime)
        {
            _pendingSeek = null;
            return false;
        }
        return true;
    }

    private void SetVolume(double fraction) =>
        Send(player => player.SetProperty("volume", (fraction * 100).ToString("0", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Отправить команду плееру. Ошибку mpv здесь не считаем поводом падать: например, перемотка
    /// файла, который перематывать нельзя, просто не сработает.
    /// </summary>
    private void Send(Action<MpvPlayer> action)
    {
        if (_player is null)
            return;
        try
        {
            action(_player);
        }
        catch (MpvException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }

    /// <summary>Время в виде 01:58:03.</summary>
    internal static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => Send(player => player.Command("cycle", "pause"));

    // Стоп: в начало файла и на паузу — на экране остаётся первый кадр.
    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        Send(player =>
        {
            player.SetProperty("pause", "yes");
            player.Command("seek", "0", "absolute");
        });
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => Send(player => player.Command("cycle", "mute"));

    private void OnPreviousClick(object sender, RoutedEventArgs e) => PreviousClicked?.Invoke();

    private void OnNextClick(object sender, RoutedEventArgs e) => NextClicked?.Invoke();

    private void OnAudioClick(object sender, RoutedEventArgs e) => AudioClicked?.Invoke((FrameworkElement)sender);

    private void OnSubtitlesClick(object sender, RoutedEventArgs e) => SubtitlesClicked?.Invoke((FrameworkElement)sender);

    private void OnPlaylistClick(object sender, RoutedEventArgs e) => PlaylistClicked?.Invoke();

    private void OnCloseProgramClick(object sender, RoutedEventArgs e) => CloseProgramClicked?.Invoke();
}
