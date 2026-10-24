using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kadr.Mpv;
using Microsoft.Win32;
using static Kadr.NativeMethods;

namespace Kadr;

// Меню окна видео: контекстное по правой кнопке (CLAUDE.md, раздел 13.1) и выпадающие
// «Аудио» / «Субтитры» кнопок панели (раздел 4.5). Меню строятся заново при каждом открытии —
// так в них всегда текущие дорожки, скорость и галочки.
public partial class VideoWindow
{
    /// <summary>Скорости в меню «Скорость».</summary>
    private static readonly double[] MenuSpeeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    /// <summary>Размеры в меню «Размер окна» — доли исходного размера картинки.</summary>
    private static readonly double[] MenuScales = [0.5, 1.0, 2.0];

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    // ---------- Контекстное меню окна видео ----------

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        var menu = BuildVideoMenu();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Состав и порядок пунктов утверждены пользователем (раздел 13.1).</summary>
    private ContextMenu BuildVideoMenu()
    {
        var hasVideo = _videoSize is not null;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Открыть файл…", OpenFileFromDialog));
        menu.Items.Add(Item("Открыть папку…", OpenFolderFromDialog));
        menu.Items.Add(new Separator());
        menu.Items.Add(Submenu("Аудио", AudioItems()));
        menu.Items.Add(Submenu("Субтитры",
            [.. SubtitleItems(), new Separator(), Item("Загрузить файл субтитров…", LoadSubtitlesFromDialog)]));
        menu.Items.Add(Submenu("Скорость", SpeedItems()));
        // В полном экране размер окна задаёт экран, поэтому пункт недоступен.
        menu.Items.Add(Submenu("Размер окна", SizeItems(), enabled: hasVideo && !IsFullscreen));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Поверх всех окон", ToggleTopmost, "Ctrl+A", isChecked: Topmost));
        menu.Items.Add(Item("Полный экран", ToggleFullscreen, "F", isChecked: IsFullscreen));
        menu.Items.Add(Item("Скриншот", () => _actions?.Screenshot(_playlist.Current), "P", enabled: hasVideo));
        menu.Items.Add(Item("Показать/скрыть панель", () => ToggleVisible(_panel), "Tab"));
        menu.Items.Add(Item("Показать/скрыть плейлист", () => ToggleVisible(_playlistWindow), "L"));
        menu.Items.Add(Item("Информация о файле", ShowFileInfo, enabled: _playlist.Current is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Выход", Close, "F10"));
        return menu;
    }

    private List<Control> SpeedItems()
    {
        double current = 1;
        if (_player is { } player)
            MpvPlayer.Try(() => current = ParseNumber(player.GetProperty("speed")) ?? 1);
        return
        [
            .. MenuSpeeds.Select(speed => Item($"{speed.ToString("0.0#", Russian)}×",
                () => _actions?.SetSpeed(speed), isChecked: Math.Abs(speed - current) < 0.001)),
        ];
    }

    private List<Control> SizeItems() =>
    [
        .. MenuScales.Select(scale => Item($"{scale * 100:0}%", () => SetVideoScale(scale))),
        Item("Вписать в экран", FitToScreen),
    ];

    // ---------- Выпадающие меню кнопок «Аудио» и «Субтитры» на панели ----------

    private void ShowAudioMenu(FrameworkElement button) => OpenAbove(button, AudioItems());

    private void ShowSubtitlesMenu(FrameworkElement button) => OpenAbove(button, SubtitleItems());

    private List<Control> AudioItems()
    {
        var audio = _tracks?.AudioTracks() ?? [];
        if (audio.Count == 0)
            return [new MenuItem { Header = "Звуковых дорожек нет", IsEnabled = false }];
        return [.. audio.Select(track => TrackItem(track.Label, track.Selected, () => _tracks?.SelectAudio(track)))];
    }

    /// <summary>Дорожки субтитров, разделитель, «Выкл».</summary>
    private List<Control> SubtitleItems()
    {
        var subtitles = _tracks?.SubtitleTracks() ?? [];
        List<Control> items =
            [.. subtitles.Select(track => TrackItem(track.Label, track.Selected, () => _tracks?.SelectSubtitles(track)))];
        if (subtitles.Count > 0)
            items.Add(new Separator());
        items.Add(TrackItem("Выкл", !subtitles.Any(t => t.Selected), () => _tracks?.SelectSubtitles(null)));
        return items;
    }

    private static void OpenAbove(FrameworkElement button, IEnumerable<Control> items)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
        foreach (var item in items)
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    // ---------- Пункты меню ----------

    private static MenuItem Item(string header, Action onClick, string? gesture = null,
        bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            IsChecked = isChecked,
            IsEnabled = enabled,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static MenuItem Submenu(string header, IEnumerable<Control> items, bool enabled = true)
    {
        var submenu = new MenuItem { Header = header, IsEnabled = enabled };
        foreach (var item in items)
            submenu.Items.Add(item);
        return submenu;
    }

    private static MenuItem TrackItem(string label, bool isChecked, Action onClick)
    {
        // Подпись — в TextBlock: иначе «_» в названии дорожки WPF принял бы за подчёркнутую букву.
        var item = new MenuItem { Header = new TextBlock { Text = label }, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ---------- Действия пунктов меню ----------

    private void OpenFileFromDialog()
    {
        var dialog = new OpenFileDialog { Title = "Kadr — открыть видео", Filter = VideoFiles.DialogFilter };
        if (dialog.ShowDialog(this) == true)
            OpenFile(dialog.FileName);
    }

    private void OpenFolderFromDialog()
    {
        var dialog = new OpenFolderDialog { Title = "Kadr — открыть папку (со всеми подпапками)" };
        if (dialog.ShowDialog(this) == true)
            OpenFolder(dialog.FolderName);
    }

    private void LoadSubtitlesFromDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Kadr — загрузить субтитры",
            Filter = "Субтитры|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.sup;*.smi|Все файлы|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            _tracks?.LoadSubtitles(dialog.FileName);
    }

    /// <summary>«Размер окна ▸ 50% / 100% / 200%»: доля исходного размера картинки, центр на месте.</summary>
    private void SetVideoScale(double scale)
    {
        if (_videoSize is not { } size || IsFullscreen)
            return;
        ResizeAroundCenter(size.Width * scale / GetBounds(Hwnd).Width);
    }

    /// <summary>«Вписать в экран»: самое большое окно с пропорциями видео, по центру рабочей области.</summary>
    private void FitToScreen()
    {
        if (IsFullscreen)
            return;
        var work = GetMonitorOf(Hwnd).WorkArea;
        var (width, height) = FitSize(work.Width);
        SetBounds(Hwnd,
            (int)Math.Round(work.Left + (work.Width - width) / 2), (int)Math.Round(work.Top + (work.Height - height) / 2),
            (int)Math.Round(width), (int)Math.Round(height));
    }

    /// <summary>«Информация о файле»: имя, размер, длительность, видео, звук, субтитры.</summary>
    private void ShowFileInfo()
    {
        if (_player is not { } player || _playlist.Current is not { } item)
            return;

        string? Get(string property)
        {
            string? value = null;
            MpvPlayer.Try(() => value = player.GetProperty(property));
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        List<(string Label, string Value)> rows = [("Файл", item.Name)];
        if (!IsUrl(item.Path))
        {
            rows.Add(("Папка", Path.GetDirectoryName(item.Path) ?? ""));
            if (File.Exists(item.Path))
                rows.Add(("Размер", FormatFileSize(new FileInfo(item.Path).Length)));
        }
        if (ParseNumber(Get("duration")) is { } duration)
            rows.Add(("Длительность", ControlPanelWindow.FormatTime(duration)));
        if (Get("file-format") is { } format)
            rows.Add(("Формат", format));

        List<string> video = [];
        if (Get("width") is { } width && Get("height") is { } height)
            video.Add($"{width}×{height}");
        if (Get("current-tracks/video/codec") is { } videoCodec)
            video.Add(videoCodec.ToUpperInvariant());
        if (ParseNumber(Get("container-fps")) is { } fps)
            video.Add($"{fps.ToString("0.###", Russian)} кадр/с");
        if (video.Count > 0)
            rows.Add(("Видео", string.Join(", ", video)));

        var audio = _tracks?.AudioTracks().FirstOrDefault(t => t.Selected);
        if (audio is not null)
        {
            var samplerate = ParseNumber(Get("audio-params/samplerate"));
            rows.Add(("Звук", samplerate is { } hz ? $"{audio.Label}, {hz:0} Гц" : audio.Label));
        }

        var subtitles = _tracks?.SubtitleTracks() ?? [];
        rows.Add(("Субтитры", subtitles.Count == 0 ? "нет"
            : $"{subtitles.FirstOrDefault(t => t.Selected)?.Label ?? "выключены"} (дорожек: {subtitles.Count})"));

        new FileInfoWindow(rows) { Owner = this }.ShowDialog();
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.0", Russian)} ГБ",
        >= 1L << 20 => $"{(bytes / (double)(1L << 20)).ToString("0", Russian)} МБ",
        _ => $"{(bytes / 1024.0).ToString("0", Russian)} КБ",
    };
}
