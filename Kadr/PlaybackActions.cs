using System.Globalization;
using System.IO;
using Kadr.Mpv;

namespace Kadr;

/// <summary>
/// Действия горячих клавиш над воспроизведением (CLAUDE.md, разделы 8 и 9):
/// пауза, перемотка, громкость, скорость, скриншот. Почти каждое показывает надпись на видео.
/// </summary>
internal sealed class PlaybackActions(MpvPlayer player)
{
    /// <summary>Длинная перемотка (клавиши «,» и «.»), секунды.</summary>
    public const double LongSeek = 20;

    /// <summary>Короткая перемотка (стрелки ← →), секунды.</summary>
    public const double ShortSeek = 5;

    /// <summary>Шаг громкости (стрелки ↑ ↓), проценты.</summary>
    public const double VolumeStep = 5;

    /// <summary>Шаг скорости (клавиши [ и ]).</summary>
    public const double SpeedStep = 0.1;

    private const double MinSpeed = 0.5;
    private const double MaxSpeed = 2.0;

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public void TogglePause() => MpvPlayer.Try(() => player.Command("cycle", "pause"));

    /// <summary>Перемотать ровно на <paramref name="seconds"/> вперёд (или назад, если меньше нуля).</summary>
    /// <remarks>
    /// «exact» обязателен: без него mpv перематывает к ближайшему опорному кадру, а в фильмах они
    /// бывают раз в 5–10 секунд — и «+5 секунд» превращается в +10.
    /// </remarks>
    public void Seek(double seconds) =>
        MpvPlayer.Try(() => player.Command("seek", Invariant(seconds), "relative+exact"));

    public void ChangeVolume(double delta)
    {
        var volume = Math.Clamp(Math.Round((ReadNumber("volume") ?? 100) + delta), 0, 100);
        if (MpvPlayer.Try(() => player.SetProperty("volume", Invariant(volume))))
            Osd($"Громкость: {volume:0}%");
    }

    public void ToggleMute()
    {
        if (MpvPlayer.Try(() => player.Command("cycle", "mute")))
            Osd(ReadText("mute") == "yes" ? "Звук выключен" : "Звук включён");
    }

    /// <summary>Быстрее / медленнее на <paramref name="delta"/>, в пределах 0,5–2,0.</summary>
    public void ChangeSpeed(double delta) =>
        SetSpeed(Math.Clamp(Math.Round((ReadNumber("speed") ?? 1) + delta, 1), MinSpeed, MaxSpeed));

    public void ResetSpeed() => SetSpeed(1);

    /// <summary>
    /// Скриншот кадра в PNG в папку «Изображения»: «Фильм — 00-42-17.png».
    /// Субтитры попадают на снимок, только если они сейчас включены — как на экране.
    /// </summary>
    public void Screenshot(PlaylistItem? item)
    {
        if (item is null)
            return;

        var position = TimeSpan.FromSeconds(Math.Max(0, ReadNumber("time-pos") ?? 0));
        // Двоеточия в именах файлов Windows не разрешает — время пишется через дефис.
        var time = $"{(int)position.TotalHours:00}-{position.Minutes:00}-{position.Seconds:00}";
        var name = SafeFileName(Path.GetFileNameWithoutExtension(item.Name));
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var path = Path.Combine(folder, $"{name} — {time}.png");
        for (int copy = 2; File.Exists(path); copy++)
            path = Path.Combine(folder, $"{name} — {time} ({copy}).png");

        // «subtitles» — снимок как на экране: с субтитрами, если они видны, но без надписей OSD.
        Osd(MpvPlayer.Try(() => player.Command("screenshot-to-file", path, "subtitles"))
            ? "Скриншот сохранён"
            : "Не удалось сохранить скриншот");
    }

    /// <summary>Надпись в углу видео на 2 секунды.</summary>
    public void Osd(string text) => MpvPlayer.Try(() => player.ShowText(text));

    private void SetSpeed(double speed)
    {
        if (MpvPlayer.Try(() => player.SetProperty("speed", Invariant(speed))))
            Osd($"Скорость: {speed.ToString("0.0", Russian)}×");
    }

    private string? ReadText(string property)
    {
        string? value = null;
        MpvPlayer.Try(() => value = player.GetProperty(property));
        return value;
    }

    private double? ReadNumber(string property) =>
        double.TryParse(ReadText(property), CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string SafeFileName(string name)
    {
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name.Length > 0 ? name : "Kadr";
    }
}
